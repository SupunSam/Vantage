using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Services;

/// <summary>ServiceNowReference is the ticket the change was made under; it goes into the audit log, not the user record.</summary>
public sealed record NewUserInput(
    string Email, string? FirstName, string? LastName, string? ContactNumber, string? TimeZone,
    string? TableauUserName, string? ServiceNowReference, IReadOnlyList<int> RoleIds);

public sealed record UpdateUserInput(
    string? FirstName, string? LastName, string? DisplayName, string? ContactNumber, string? TimeZone,
    string? TableauUserName, string? ServiceNowReference, UserStatus Status, IReadOnlyList<int> RoleIds);

public sealed record BulkResult(string Email, string Outcome, string? Detail, int? UserId);

/// <summary>
/// User Master rules. Email is the unique key. Internal users (internal email domain) are filled from the HRMS table;
/// external users start as PendingSetup and complete their details at first login. Everyone has Dashboard User.
/// Status changed by an admin is marked manual, so the HRMS job never overrides it. The last active Super Admin
/// can't be deactivated or lose the role.
/// </summary>
public sealed class UserService(AppDbContext db, AuditWriter audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<string> InternalDomainAsync(CancellationToken ct) =>
        await db.SystemSettings.Where(s => s.Key == SettingKeys.InternalEmailDomain).Select(s => s.Value).SingleOrDefaultAsync(ct) ?? "rrd.com";

    public static string? NormaliseEmail(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (e.Length is 0 or > 256 || !MailAddress.TryCreate(e, out var parsed) || parsed.Address != e || !e.Contains('.')) return null;
        return e;
    }

    public async Task<User> CreateAsync(NewUserInput input, int? actorId, CancellationToken ct)
    {
        var email = NormaliseEmail(input.Email) ?? throw new RuleException($"'{input.Email}' is not a valid email address.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new RuleException($"{email} is already a user.");

        var user = await BuildAsync(email, input.FirstName, input.LastName, ct);
        user.ContactNumber = Blank(input.ContactNumber);
        user.TimeZone = Blank(input.TimeZone) ?? user.TimeZone;
        user.TableauUserName = Blank(input.TableauUserName);
        await SetRolesAsync(user, input.RoleIds, ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        audit.Add("user.created", "User", user.Id, details: new { user.Email, type = user.UserType.ToString(), hrms = user.HrmsProfile is not null }, serviceNowReference: input.ServiceNowReference);
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Adds many users from a pasted list or an Excel file. Existing users are skipped, never changed.</summary>
    public async Task<IReadOnlyList<BulkResult>> BulkCreateAsync(IEnumerable<(string Email, string? RoleName)> rows, IReadOnlyList<int> roleIdsForAll, int? actorId, CancellationToken ct,
        string? serviceNowReference = null)
    {
        var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase, ct);
        var results = new List<BulkResult>();
        var seen = new HashSet<string>();
        foreach (var (raw, roleName) in rows)
        {
            var email = NormaliseEmail(raw);
            if (email is null) { results.Add(new(raw.Trim(), "Invalid", "Not a valid email address.", null)); continue; }
            if (!seen.Add(email)) { results.Add(new(email, "Skipped", "Listed twice.", null)); continue; }

            var existing = await db.Users.Where(u => u.Email == email).Select(u => (int?)u.Id).SingleOrDefaultAsync(ct);
            if (existing is not null) { results.Add(new(email, "Skipped", "Already a user.", existing)); continue; }

            var roleIds = roleIdsForAll.ToList();
            string? warning = null;
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                if (roles.TryGetValue(roleName.Trim(), out var rid)) roleIds.Add(rid);
                else warning = $"Role '{roleName.Trim()}' not found; added without it.";
            }

            var user = await BuildAsync(email, null, null, ct);
            await SetRolesAsync(user, roleIds, ct);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            if (user.UserType == UserType.Internal && user.HrmsProfile is null)
                warning = (warning is null ? "" : warning + " ") + "Internal email not found in HRMS; names left blank.";
            var note = user.UserType == UserType.External ? "External: completes details at first login."
                : user.Status == UserStatus.Inactive ? "Filled from HRMS. HRMS lists them as a leaver, so they're Inactive."
                : "Filled from HRMS.";
            audit.Add("user.created", "User", user.Id, details: new { user.Email, type = user.UserType.ToString(), bulk = true }, serviceNowReference: serviceNowReference);
            results.Add(new(email, "Added", warning ?? note, user.Id));
        }
        audit.Add("user.bulk-added", "User", null, details: new
        {
            added = results.Count(r => r.Outcome == "Added"), total = results.Count,
            emails = results.Where(r => r.Outcome == "Added").Select(r => r.Email).Take(200).ToList(),
        }, serviceNowReference: serviceNowReference);
        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<User> UpdateAsync(int id, UpdateUserInput input, int? actorId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();

        var superAdminId = await db.Roles.Where(r => r.Name == SystemRoles.SuperAdmin).Select(r => r.Id).SingleAsync(ct);
        var isSuperAdmin = user.Roles.Any(r => r.RoleId == superAdminId);
        var staysSuperAdmin = input.RoleIds.Contains(superAdminId) && input.Status == UserStatus.Active;
        if (isSuperAdmin && !staysSuperAdmin)
        {
            var others = await db.UserRoles.CountAsync(r => r.RoleId == superAdminId && r.UserId != id && r.User.Status == UserStatus.Active, ct);
            if (others == 0) throw new RuleException("This is the only active Super Admin. Make someone else Super Admin first.");
        }

        if (user.Status != input.Status)
        {
            audit.Add("user.status-changed", "User", user.Id, details: new { from = user.Status.ToString(), to = input.Status.ToString() }, serviceNowReference: input.ServiceNowReference);
            user.Status = input.Status;
            user.StatusSetManually = true;
        }
        user.FirstName = Blank(input.FirstName);
        user.LastName = Blank(input.LastName);
        user.DisplayName = Blank(input.DisplayName) ?? Join(user.FirstName, user.LastName);
        user.ContactNumber = Blank(input.ContactNumber);
        user.TimeZone = Blank(input.TimeZone) ?? user.TimeZone;
        user.TableauUserName = Blank(input.TableauUserName);
        user.UpdatedAtUtc = Now;

        var before = user.Roles.Select(r => r.Role.Name).OrderBy(n => n).ToList();
        await SetRolesAsync(user, input.RoleIds, ct);
        await db.SaveChangesAsync(ct);
        var after = await db.UserRoles.Where(r => r.UserId == id).Select(r => r.Role.Name).OrderBy(n => n).ToListAsync(ct);
        audit.Add("user.updated", "User", user.Id, details: new { rolesBefore = before, rolesAfter = after }, serviceNowReference: input.ServiceNowReference);
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Email is the key, so changing it is an explicit Super Admin action.</summary>
    public async Task ChangeEmailAsync(int id, string newEmail, CancellationToken ct)
    {
        var email = NormaliseEmail(newEmail) ?? throw new RuleException($"'{newEmail}' is not a valid email address.");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new KeyNotFoundException();
        if (await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct)) throw new RuleException($"{email} already belongs to another user.");
        audit.Add("user.email-changed", "User", id, details: new { from = user.Email, to = email });
        user.Email = email;
        user.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
    }

    private async Task<User> BuildAsync(string email, string? firstName, string? lastName, CancellationToken ct)
    {
        var internalDomain = await InternalDomainAsync(ct);
        var isInternal = Rules.IsInternalEmail(email, internalDomain);
        var user = new User
        {
            Email = email, UserType = isInternal ? UserType.Internal : UserType.External,
            Status = isInternal ? UserStatus.Active : UserStatus.PendingSetup,
            FirstName = Blank(firstName), LastName = Blank(lastName), CreatedAtUtc = Now,
        };

        if (isInternal)
        {
            var h = await db.HrmsSource.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
            if (h is not null)
            {
                user.FirstName ??= h.FirstName;
                user.LastName ??= h.LastName;
                user.DisplayName = h.DisplayName;
                user.TimeZone = HrmsSyncService.TimeZoneFor(h.Location) ?? user.TimeZone;
                if (h.EmploymentStatus != "Active") user.Status = UserStatus.Inactive;
                user.HrmsProfile = HrmsSyncService.ProfileFrom(h, Now);
            }
        }
        user.DisplayName ??= Join(user.FirstName, user.LastName);
        return user;
    }

    /// <summary>Sets the user's hand-assigned roles; Dashboard User is always kept, automatic roles are left alone.</summary>
    private async Task SetRolesAsync(User user, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        var dashboardUserId = await db.Roles.Where(r => r.Name == SystemRoles.DashboardUser).Select(r => r.Id).SingleAsync(ct);
        var wanted = roleIds.Append(dashboardUserId).ToHashSet();
        var known = await db.Roles.Where(r => wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (known.Count != wanted.Count) throw new RuleException("One of the chosen roles doesn't exist.");

        foreach (var ur in user.Roles.Where(r => !r.IsAutomatic && !wanted.Contains(r.RoleId)).ToList())
            user.Roles.Remove(ur);
        foreach (var rid in wanted.Where(rid => user.Roles.All(r => r.RoleId != rid)))
            user.Roles.Add(new UserRole { RoleId = rid, AssignedAtUtc = Now });
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static string? Join(string? a, string? b) => Blank($"{a} {b}");
}
