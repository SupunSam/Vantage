using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Data;

/// <summary>
/// Seeds what the portal needs to start, idempotently: the built-in roles, BI service types, default settings,
/// the approved CDN list, and one bootstrap Super Admin so someone can sign in. Everything else (users, tenants,
/// dashboards) is added through the Admin Portal.
/// </summary>
public sealed class DbSeeder(AppDbContext db, TimeProvider clock, ILogger<DbSeeder> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task SeedReferenceDataAsync(CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await SeedServiceTypesAsync(ct);
        await SeedSettingsAsync(ct);
        await SeedCdnsAsync(ct);
    }

    /// <summary>Creates the bootstrap Super Admin if no Super Admin exists yet. Filled from HRMS when the email is there.</summary>
    public async Task SeedBootstrapSuperAdminAsync(string email, CancellationToken ct = default)
    {
        var superAdmin = await db.Roles.SingleAsync(r => r.Name == SystemRoles.SuperAdmin, ct);
        if (await db.UserRoles.AnyAsync(r => r.RoleId == superAdmin.Id, ct)) return;

        email = email.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            var h = await db.HrmsSource.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
            user = new User
            {
                Email = email, UserType = UserType.Internal, Status = UserStatus.Active, CreatedAtUtc = Now,
                FirstName = h?.FirstName, LastName = h?.LastName, DisplayName = h?.DisplayName ?? email,
                TimeZone = HrmsSyncService.TimeZoneFor(h?.Location) ?? "Asia/Colombo",
                HrmsProfile = h is null ? null : HrmsSyncService.ProfileFrom(h, Now),
            };
            db.Users.Add(user);
        }
        user.Status = UserStatus.Active;
        var dashboardUser = await db.Roles.SingleAsync(r => r.Name == SystemRoles.DashboardUser, ct);
        foreach (var role in new[] { superAdmin, dashboardUser })
            if (user.Roles.All(r => r.RoleId != role.Id))
                user.Roles.Add(new UserRole { Role = role, AssignedAtUtc = Now });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Bootstrap Super Admin: {Email}", email);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var all = AppModules.All.Select(m => m.Key).ToArray();
        var definitions = new (string Name, string Description, Dictionary<string, PermissionLevel> Perms)[]
        {
            (SystemRoles.SuperAdmin, "Full access to both portals. Always has Edit on every module.", all.ToDictionary(k => k, _ => PermissionLevel.Edit)),
            (SystemRoles.DashboardOwner, "Given automatically to primary and backup owners of a dashboard.", new()
            {
                [AppModules.Dashboards] = PermissionLevel.Edit,
                [AppModules.Catalogue] = PermissionLevel.Edit,
                [AppModules.OwnerWorkspace] = PermissionLevel.Edit,
            }),
            (SystemRoles.DashboardUser, "Every signed-in user: opens dashboards they have access to and requests access.", new()
            {
                [AppModules.Dashboards] = PermissionLevel.Edit,
                [AppModules.Catalogue] = PermissionLevel.Edit,
            }),
        };

        foreach (var (name, description, perms) in definitions)
        {
            var role = await db.Roles.Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Name == name, ct);
            if (role is null)
            {
                db.Roles.Add(new Role
                {
                    Name = name, Description = description, IsSystem = true, CreatedAtUtc = Now,
                    Permissions = perms.Select(p => new RolePermission { Module = p.Key, Level = p.Value }).ToList(),
                });
            }
            else if (name == SystemRoles.SuperAdmin)
            {
                // Keep Super Admin complete when new modules are added.
                foreach (var key in all.Where(k => role.Permissions.All(p => p.Module != k)))
                    role.Permissions.Add(new RolePermission { Module = key, Level = PermissionLevel.Edit });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedServiceTypesAsync(CancellationToken ct)
    {
        var types = new[]
        {
            new BiTypeConfig { Type = BiType.PowerBi, DisplayName = "Power BI", RequiresFile = true, RequiresTenant = true, SupportsRls = true, SupportsRefresh = true, AllowedExtensions = ".pbix", MaxFileSizeMb = 1024 },
            new BiTypeConfig { Type = BiType.Tableau, DisplayName = "Tableau", RequiresUrl = true, RequiresTenant = true },
            new BiTypeConfig { Type = BiType.GenAi, DisplayName = "GenAI Dashboard", RequiresFile = true, AllowedExtensions = ".html", MaxFileSizeMb = 5 },
        };
        foreach (var t in types)
            if (!await db.BiTypes.AnyAsync(x => x.Type == t.Type, ct)) db.BiTypes.Add(t);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        foreach (var (key, value, description) in SettingKeys.Defaults)
            if (!await db.SystemSettings.AnyAsync(s => s.Key == key, ct))
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = description, UpdatedAtUtc = Now });
        var retired = await db.SystemSettings.Where(s => SettingKeys.Retired.Contains(s.Key)).ToListAsync(ct);
        db.SystemSettings.RemoveRange(retired);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedCdnsAsync(CancellationToken ct)
    {
        foreach (var host in new[] { "cdnjs.cloudflare.com", "cdn.jsdelivr.net" })
            if (!await db.ApprovedCdns.AnyAsync(c => c.Host == host, ct))
                db.ApprovedCdns.Add(new ApprovedCdn { Host = host, Notes = "Pin versions and use integrity hashes" });
        await db.SaveChangesAsync(ct);
    }
}
