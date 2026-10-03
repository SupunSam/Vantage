using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Access requests. Every request needs approval by one of the dashboard's owners (or a Super Admin on their
/// behalf); there is no auto-approval. The approver picks the access group; requesters never see or choose groups.
/// One pending request per user per dashboard (also a filtered unique index).
/// </summary>
public sealed class AccessRequestService(
    AppDbContext db, AccessGroupService access, NotificationService notifications, EmailOutboxService email, AuditWriter audit, TimeProvider clock)
{
    public const string OwnerlessMessage = "This dashboard has no owner right now. Please request access again in a few days.";
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AccessRequest> CreateAsync(int dashboardId, int requesterId, string? comment, CancellationToken ct = default)
    {
        var d = await db.Dashboards.SingleOrDefaultAsync(x => x.Id == dashboardId, ct) ?? throw new KeyNotFoundException();
        var requester = await db.Users.SingleAsync(u => u.Id == requesterId, ct);
        if (d.Status != DashboardStatus.Active) throw new RuleException("This dashboard isn't available right now.");
        if (!await IsVisibleAsync(d, requester, ct)) throw new KeyNotFoundException();
        if (d.OwnershipPendingReview || (d.PrimaryOwnerId is null && d.BackupOwnerId is null)) throw new RuleException(OwnerlessMessage);

        var membership = await db.GroupMembers.Include(m => m.Group)
            .SingleOrDefaultAsync(m => m.DashboardId == dashboardId && m.UserId == requesterId && m.RemovedAtUtc == null, ct);
        if (membership is { Group.Status: GroupStatus.Active }) throw new RuleException("You already have access to this dashboard.");
        if (await db.AccessRequests.AnyAsync(r => r.DashboardId == dashboardId && r.RequesterUserId == requesterId && r.Status == AccessRequestStatus.Pending, ct))
            throw new RuleException("You've already asked for access. The owner will decide soon.");

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (text?.Length > 1000) throw new RuleException("The reason is up to 1,000 characters.");
        var request = new AccessRequest { DashboardId = dashboardId, RequesterUserId = requesterId, RequesterComment = text, RequestedAtUtc = Now };
        db.AccessRequests.Add(request);
        await db.SaveChangesAsync(ct);

        var who = requester.DisplayName ?? requester.Email;
        var owners = Owners(d);
        audit.Add("access-request.created", "AccessRequest", request.Id, d.Id, new { requester = requester.Email, comment = text });
        notifications.Notify(owners, "access-request.created", $"{who} asked for access to {d.Name}", text, "/approvals");
        await email.QueueAsync(owners, "access-request.created", $"{who} asked for access to {d.Name}", "New access request",
            $"<p><strong>{EmailOutboxService.Encode(who)}</strong> ({EmailOutboxService.Encode(requester.Email)}) asked for access to <strong>{EmailOutboxService.Encode(d.Name)}</strong>.</p>"
            + (text is null ? "" : $"<p>Their reason: <em>{EmailOutboxService.Encode(text)}</em></p>")
            + "<p>As an owner of this dashboard, please approve or reject the request. When you approve, you choose which access group they join.</p>",
            "Review the Request", $"{email.Options.UserPortalUrl}/approvals", ct);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task CancelAsync(int requestId, int requesterId, CancellationToken ct = default)
    {
        var r = await db.AccessRequests.SingleOrDefaultAsync(x => x.Id == requestId && x.RequesterUserId == requesterId, ct) ?? throw new KeyNotFoundException();
        if (r.Status != AccessRequestStatus.Pending) throw new RuleException("Only a pending request can be withdrawn.");
        r.Status = AccessRequestStatus.Cancelled;
        r.DecidedAtUtc = Now;
        r.DecidedByUserId = requesterId;
        audit.Add("access-request.cancelled", "AccessRequest", r.Id, r.DashboardId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Approves a request into a group the approver picks. Dashboards without RLS have only their default group, so
    /// it is used when no group is given. On RLS dashboards the approver must choose (the default group usually
    /// carries the owners' see-everything role).
    /// </summary>
    public async Task ApproveAsync(int requestId, int? groupId, string? note, GroupActor actor, CancellationToken ct = default)
    {
        var r = await LoadDecidableAsync(requestId, actor, ct);
        var d = r.Dashboard;
        DashboardGroup group;
        if (groupId is { } gid)
        {
            group = await db.DashboardGroups.SingleOrDefaultAsync(g => g.Id == gid && g.DashboardId == d.Id, ct)
                ?? throw new RuleException("Choose one of this dashboard's access groups.");
        }
        else
        {
            if (d.RlsEnabled) throw new RuleException("This dashboard uses row-level security, so choose the access group that gives the right data.");
            group = await db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault, ct);
        }
        if (group.Status != GroupStatus.Active) throw new RuleException($"{group.Name} is inactive. Choose an active group.");
        if (d.RlsEnabled && string.IsNullOrWhiteSpace(group.RlsValue)) throw new RuleException($"{group.Name} has no RLS value yet, so its members couldn't open the dashboard.");

        var result = (await access.AddMembersAsync(group.Id, [r.Requester.Email], moveFromOtherGroups: true, MembershipSource.AccessRequest, actor, ct, notify: false)).Single();
        if (result.Outcome is not ("Added" or "Moved" or "AlreadyHere")) throw new RuleException(result.Detail ?? "The requester couldn't be added to the group.");

        r.Status = AccessRequestStatus.Approved;
        r.AssignedGroupId = group.Id;
        r.DecidedByUserId = actor.UserId;
        r.DecidedAtUtc = Now;
        r.DecisionNote = Clean(note);
        audit.Add("access-request.approved", "AccessRequest", r.Id, d.Id, new { requester = r.Requester.Email, group = group.Name, note = r.DecisionNote, onBehalf = !IsOwner(d, actor.UserId) });
        notifications.Notify([r.RequesterUserId], "access-request.approved", $"You can now open {d.Name}", r.DecisionNote ?? "Your access request was approved.", $"/dashboards/{d.Id}");
        await email.QueueAsync([r.RequesterUserId], "access-request.approved", $"Access to {d.Name} approved", "Your access request was approved",
            $"<p>You can now open <strong>{EmailOutboxService.Encode(d.Name)}</strong>.</p>"
            + (r.DecisionNote is null ? "" : $"<p>Note from the approver: <em>{EmailOutboxService.Encode(r.DecisionNote)}</em></p>"),
            "Open the Dashboard", $"{email.Options.UserPortalUrl}/dashboards/{d.Id}", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectAsync(int requestId, string? note, GroupActor actor, CancellationToken ct = default)
    {
        var r = await LoadDecidableAsync(requestId, actor, ct);
        var d = r.Dashboard;
        r.Status = AccessRequestStatus.Rejected;
        r.DecidedByUserId = actor.UserId;
        r.DecidedAtUtc = Now;
        r.DecisionNote = Clean(note);
        audit.Add("access-request.rejected", "AccessRequest", r.Id, d.Id, new { requester = r.Requester.Email, note = r.DecisionNote, onBehalf = !IsOwner(d, actor.UserId) });
        notifications.Notify([r.RequesterUserId], "access-request.rejected", $"Your request for {d.Name} wasn't approved", r.DecisionNote, "/catalogue");
        await email.QueueAsync([r.RequesterUserId], "access-request.rejected", $"Access to {d.Name} not approved", "Your access request wasn't approved",
            $"<p>The owner of <strong>{EmailOutboxService.Encode(d.Name)}</strong> didn't approve your request.</p>"
            + (r.DecisionNote is null ? "" : $"<p>Their note: <em>{EmailOutboxService.Encode(r.DecisionNote)}</em></p>")
            + "<p>If you think you need this dashboard, contact the owner.</p>", null, null, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether a user can see the dashboard in the catalogue (external users and Audience = Internal is a setting).</summary>
    public async Task<bool> IsVisibleAsync(Dashboard d, User user, CancellationToken ct)
    {
        if (d.Audience != Audience.Internal || user.UserType == UserType.Internal) return true;
        var setting = await db.SystemSettings.Where(s => s.Key == SettingKeys.ExternalSeeInternalCatalogue).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOwner(Dashboard d, int userId) => d.PrimaryOwnerId == userId || d.BackupOwnerId == userId;

    private async Task<AccessRequest> LoadDecidableAsync(int requestId, GroupActor actor, CancellationToken ct)
    {
        var r = await db.AccessRequests.Include(x => x.Dashboard).Include(x => x.Requester).SingleOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw new KeyNotFoundException();
        if (!actor.IsSuperAdmin && !IsOwner(r.Dashboard, actor.UserId)) throw new RuleException("Only the dashboard's owners or a Super Admin can decide this request.");
        if (r.Status != AccessRequestStatus.Pending) throw new RuleException($"This request was already {r.Status.ToString().ToLowerInvariant()}.");
        if (r.Dashboard.OwnershipPendingReview && !actor.IsSuperAdmin)
            throw new RuleException("This dashboard's owners are being reviewed, so approvals are paused until a Super Admin confirms them.");
        return r;
    }

    private static IEnumerable<int> Owners(Dashboard d) => new[] { d.PrimaryOwnerId, d.BackupOwnerId }.OfType<int>();

    private static string? Clean(string? note)
    {
        var t = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (t?.Length > 1000) throw new RuleException("The note is up to 1,000 characters.");
        return t;
    }
}
