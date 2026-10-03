using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>Who is making an access change. Super Admins may act while a dashboard's owners are under review.</summary>
public sealed record GroupActor(int UserId, bool IsSuperAdmin);

/// <summary>One line of the outcome of adding or copying members: Added, Moved, AlreadyHere, InOtherGroup, NotAUser, Inactive, Invalid.</summary>
public sealed record MemberResult(string Email, string Outcome, string? Detail);

/// <summary>
/// Access Groups: members, moves between groups, status, rename, copying members and cloning groups.
/// One live group per user per dashboard (also enforced by a filtered unique index). Memberships are never
/// hard-deleted: removing sets RemovedAtUtc, so the history and audit trail stay complete.
/// </summary>
public sealed class AccessGroupService(
    AppDbContext db, GroupService groups, NotificationService notifications, AuditWriter audit, TimeProvider clock)
{
    public const int MaxBatch = 5000;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Adds portal users by email. Only existing users can be added: anyone who isn't a user yet is listed as
    /// NotAUser and must be added in Users first. People already in another group of the same dashboard are
    /// skipped, or moved here when <paramref name="moveFromOtherGroups"/>.
    /// </summary>
    public async Task<List<MemberResult>> AddMembersAsync(int groupId, IEnumerable<string> emails, bool moveFromOtherGroups,
        MembershipSource source, GroupActor actor, CancellationToken ct = default, string? serviceNowReference = null, bool notify = true)
    {
        var group = await LoadEditableAsync(groupId, actor, ct);
        var list = emails.Select(e => e?.Trim() ?? "").Where(e => e.Length > 0).ToList();
        if (list.Count == 0) throw new RuleException("Enter at least one email address.");
        if (list.Count > MaxBatch) throw new RuleException($"Up to {MaxBatch:N0} people at a time.");

        var results = new List<MemberResult>();
        var seen = new HashSet<string>();
        var valid = new List<string>();
        foreach (var raw in list)
        {
            var email = UserService.NormaliseEmail(raw);
            if (email is null) { results.Add(new(raw, "Invalid", "Not a valid email address.")); continue; }
            if (seen.Add(email)) valid.Add(email);
        }

        var known = await db.Users.Where(u => valid.Contains(u.Email)).Select(u => new { u.Id, u.Email, u.Status }).ToListAsync(ct);
        var ids = known.Select(k => k.Id).ToList();
        var live = await db.GroupMembers.Include(m => m.Group)
            .Where(m => m.DashboardId == group.DashboardId && m.RemovedAtUtc == null && ids.Contains(m.UserId))
            .ToListAsync(ct);

        var addedIds = new List<int>();
        var grantedIds = new List<int>();
        foreach (var email in valid)
        {
            var u = known.SingleOrDefault(k => k.Email == email);
            if (u is null) { results.Add(new(email, "NotAUser", "Not a portal user yet. Add them in Users first, then add them here.")); continue; }
            if (u.Status == UserStatus.Inactive) { results.Add(new(email, "Inactive", "This user is inactive.")); continue; }
            var current = live.SingleOrDefault(m => m.UserId == u.Id);
            if (current?.GroupId == groupId) { results.Add(new(email, "AlreadyHere", null)); continue; }
            if (current is not null && !moveFromOtherGroups)
            {
                results.Add(new(email, "InOtherGroup", $"Already in {current.Group.Name}. Tick \"Move people from other groups\" or use Move."));
                continue;
            }
            if (current is not null)
            {
                End(current, actor.UserId, $"Moved to {group.Name}");
                await db.SaveChangesAsync(ct); // free the one-live-group slot first
                results.Add(new(email, "Moved", $"Moved from {current.Group.Name}."));
            }
            else
            {
                results.Add(new(email, "Added", null));
                grantedIds.Add(u.Id);
            }
            db.GroupMembers.Add(new GroupMember { GroupId = groupId, DashboardId = group.DashboardId, UserId = u.Id, Source = source, AddedAtUtc = Now, AddedByUserId = actor.UserId });
            addedIds.Add(u.Id);
        }

        if (addedIds.Count > 0)
        {
            audit.Add("group.members-added", "DashboardGroup", groupId, group.DashboardId, new
            {
                group = group.Name, count = addedIds.Count, moved = results.Count(r => r.Outcome == "Moved"),
                emails = results.Where(r => r.Outcome is "Added" or "Moved").Select(r => r.Email).Take(50).ToList(),
            }, serviceNowReference);
            if (notify) NotifyGranted(group, grantedIds);
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Removes someone from the group (their membership is ended, not deleted). Owners can't be removed while they own the dashboard.</summary>
    public async Task RemoveMemberAsync(int groupId, int userId, GroupActor actor, CancellationToken ct = default)
    {
        var group = await LoadEditableAsync(groupId, actor, ct);
        var member = await db.GroupMembers.Include(m => m.User)
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.RemovedAtUtc == null, ct) ?? throw new KeyNotFoundException();
        if (group.Dashboard.PrimaryOwnerId == userId || group.Dashboard.BackupOwnerId == userId)
            throw new RuleException($"{member.User.DisplayName ?? member.User.Email} owns this dashboard, so they keep access. Change the owner in Dashboards Master first, or move them to another group.");
        End(member, actor.UserId, "Removed");
        audit.Add("group.member-removed", "DashboardGroup", groupId, group.DashboardId, new { group = group.Name, email = member.User.Email });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves someone to another group of the same dashboard (for example to change which RLS role they see).</summary>
    public async Task MoveMemberAsync(int groupId, int userId, int targetGroupId, GroupActor actor, CancellationToken ct = default)
    {
        var group = await LoadEditableAsync(groupId, actor, ct);
        var target = await db.DashboardGroups.SingleOrDefaultAsync(g => g.Id == targetGroupId, ct) ?? throw new KeyNotFoundException();
        if (target.DashboardId != group.DashboardId) throw new RuleException("People can only move between groups of the same dashboard.");
        if (target.Status != GroupStatus.Active) throw new RuleException($"{target.Name} is {target.Status.ToString().ToLowerInvariant()}; activate it first.");
        if (target.Id == groupId) return;
        var member = await db.GroupMembers.Include(m => m.User)
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.RemovedAtUtc == null, ct) ?? throw new KeyNotFoundException();

        End(member, actor.UserId, $"Moved to {target.Name}");
        await db.SaveChangesAsync(ct);
        db.GroupMembers.Add(new GroupMember { GroupId = target.Id, DashboardId = group.DashboardId, UserId = userId, Source = MembershipSource.Manual, AddedAtUtc = Now, AddedByUserId = actor.UserId });
        audit.Add("group.member-moved", "DashboardGroup", groupId, group.DashboardId, new { email = member.User.Email, from = group.Name, to = target.Name });
        audit.Add("group.member-moved", "DashboardGroup", target.Id, group.DashboardId, new { email = member.User.Email, from = group.Name, to = target.Name });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Inactive groups keep their members but give no access. The default group always stays active.</summary>
    public async Task SetActiveAsync(int groupId, bool active, GroupActor actor, CancellationToken ct = default)
    {
        var group = await LoadEditableAsync(groupId, actor, ct, allowInactive: true);
        if (group.IsDefault && !active) throw new RuleException("The default group holds the owners, so it can't be deactivated.");
        var status = active ? GroupStatus.Active : GroupStatus.Inactive;
        if (group.Status == status) return;
        audit.Add(active ? "group.activated" : "group.deactivated", "DashboardGroup", groupId, group.DashboardId, new { group = group.Name });
        group.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(int groupId, string? name, GroupActor actor, CancellationToken ct = default)
    {
        var group = await LoadEditableAsync(groupId, actor, ct, allowInactive: true);
        if (group.IsDefault) throw new RuleException("The default group's name is fixed: <code>-<ID>-default.");
        var clean = (name ?? "").Trim();
        if (!Rules.IsValidGroupName(clean)) throw new RuleException("Group names are 1 to 40 letters, digits, hyphens or underscores.");
        if (clean == group.Name) return;
        if (await db.DashboardGroups.AnyAsync(g => g.Name == clean && g.Id != groupId, ct)) throw new RuleException($"The group name '{clean}' is already used.");
        audit.Add("group.renamed", "DashboardGroup", groupId, group.DashboardId, new { from = group.Name, to = clean });
        group.Name = clean;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves the group's details in one go: name (not for the default group), RLS value and active/inactive.</summary>
    public async Task UpdateDetailsAsync(int groupId, string? name, string? rlsValue, bool active, GroupActor actor, CancellationToken ct = default)
    {
        var group = await LoadEditableAsync(groupId, actor, ct, allowInactive: true);
        if (group.IsDefault && !active) throw new RuleException("The default group holds the owners, so it can't be deactivated.");
        var clean = (name ?? "").Trim();
        if (group.IsDefault && clean.Length > 0 && clean != group.Name) throw new RuleException("The default group's name is fixed: <code>-<ID>-default.");
        if (clean.Length > 0 && clean != group.Name) await RenameAsync(groupId, clean, actor, ct);
        await groups.SetRlsAsync(group.DashboardId, groupId, rlsValue, ct);
        await SetActiveAsync(groupId, active, actor, ct);
    }

    /// <summary>Copies the live members of another group (any dashboard) into this one. People already in a group of this dashboard are skipped.</summary>
    public async Task<List<MemberResult>> CopyMembersAsync(int sourceGroupId, int targetGroupId, GroupActor actor, CancellationToken ct = default)
    {
        if (sourceGroupId == targetGroupId) throw new RuleException("Choose a different group to copy from.");
        var emails = await db.GroupMembers.Where(m => m.GroupId == sourceGroupId && m.RemovedAtUtc == null).Select(m => m.User.Email).ToListAsync(ct);
        if (!await db.DashboardGroups.AnyAsync(g => g.Id == sourceGroupId, ct)) throw new KeyNotFoundException();
        if (emails.Count == 0) throw new RuleException("That group has no members to copy.");
        var results = await AddMembersAsync(targetGroupId, emails, moveFromOtherGroups: false, MembershipSource.Clone, actor, ct);
        audit.Add("group.members-copied", "DashboardGroup", targetGroupId, null, new { fromGroupId = sourceGroupId, copied = results.Count(r => r.Outcome == "Added") });
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Creates a group on an RLS dashboard, optionally copying the members of an existing group (clone: members only).</summary>
    public async Task<(DashboardGroup Group, List<MemberResult> Copied)> CreateAsync(int dashboardId, string? name, string? rlsValue, int? copyFromGroupId,
        GroupActor actor, CancellationToken ct = default)
    {
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == dashboardId, ct) ?? throw new KeyNotFoundException();
        EnsureNotPaused(dashboard, actor);
        var group = await groups.AddAsync(dashboardId, name, rlsValue, actor.UserId, ct);
        var copied = new List<MemberResult>();
        if (copyFromGroupId is { } src)
        {
            group.ClonedFromGroupId = src;
            await db.SaveChangesAsync(ct);
            var hasMembers = await db.GroupMembers.AnyAsync(m => m.GroupId == src && m.RemovedAtUtc == null, ct);
            if (hasMembers) copied = await CopyMembersAsync(src, group.Id, actor, ct);
        }
        return (group, copied);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<DashboardGroup> LoadEditableAsync(int groupId, GroupActor actor, CancellationToken ct, bool allowInactive = false)
    {
        var group = await db.DashboardGroups.Include(g => g.Dashboard).SingleOrDefaultAsync(g => g.Id == groupId, ct) ?? throw new KeyNotFoundException();
        if (group.Status == GroupStatus.Retired || group.Dashboard.Status == DashboardStatus.Retired)
            throw new RuleException("This group belongs to a retired dashboard and can't be changed.");
        if (!allowInactive && group.Status != GroupStatus.Active)
            throw new RuleException($"{group.Name} is inactive. Activate it before changing its members.");
        EnsureNotPaused(group.Dashboard, actor);
        return group;
    }

    private static void EnsureNotPaused(Dashboard d, GroupActor actor)
    {
        if (d.OwnershipPendingReview && !actor.IsSuperAdmin)
            throw new RuleException("This dashboard's owners are being reviewed, so access changes are paused until a Super Admin confirms them.");
    }

    private void End(GroupMember m, int actorId, string reason)
    {
        m.RemovedAtUtc = Now;
        m.RemovedByUserId = actorId;
        m.RemovedReason = reason;
    }

    private void NotifyGranted(DashboardGroup group, IEnumerable<int> userIds)
    {
        if (group.Dashboard.Status != DashboardStatus.Active || group.Status != GroupStatus.Active) return;
        notifications.Notify(userIds, "access.granted", $"You can now open {group.Dashboard.Name}", "It's on your home page.", $"/dashboards/{group.DashboardId}");
    }
}
