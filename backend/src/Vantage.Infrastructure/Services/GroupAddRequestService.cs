using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;

namespace Vantage.Infrastructure.Services;

/// <param name="Mode">Requested = sent to the owners; Nothing = nobody could be added or requested.</param>
public sealed record AddOutcome(string Mode, int? RequestId, List<MemberResult> Results);

public sealed record PersonRef(int Id, string Email, string? DisplayName);
public sealed record GroupAddItemRow(int UserId, string Email, string? DisplayName, string UserType, string? Title, string? Department, string Decision, string? Result, string? CurrentGroup);
public sealed record GroupAddRow(
    int Id, string Status, string Action, string? RuleName, DateTime CreatedAtUtc, string? Note, string? ServiceNowReference, bool MoveFromOtherGroups,
    DateTime? DecidedAtUtc, string? DecisionNote, string? OverrideReason, string? DecidedBy,
    PersonRef? RequestedBy, int DashboardId, string Dashboard, bool RlsEnabled, bool OwnershipPendingReview,
    int GroupId, string Group, string? RlsValue, bool GroupIsDefault,
    List<GroupAddItemRow> Items, List<string> Owners, bool CanDecide, bool NeedsOverride, bool IsRequester);

/// <summary>A person an access rule proposes to add to, or remove from, a group.</summary>
public sealed record RuleProposal(int UserId, string Email, string? Detail);

/// <summary>
/// Changing who is in an access group (C26, C29). Nobody is added or removed when an admin asks or a rule matches: the
/// whole list goes to the dashboard's owners as ONE request, and they approve everyone or only the people they tick.
/// Owners hold approve and reject only; they can't add people themselves. Only in an exception can a Super Admin decide
/// in the owners' place, from the Access Requests page, and then they must give a reason, which is saved in the audit
/// log and the group's history, and the owners are told.
/// A request is either an Add (raised by an admin, or by an add rule) or a Remove (raised only by a remove rule).
/// </summary>
public sealed class GroupAddRequestService(
    AppDbContext db, AccessGroupService access, NotificationService notifications, EmailOutboxService email, AuditWriter audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What an admin's "add these people" turns into: one pending request holding every person who could be added.
    /// People who can't be added (not portal users, inactive, already here, already waiting in another request) are
    /// reported and left out. Nobody is added until the owners decide.
    /// </summary>
    public async Task<AddOutcome> SubmitAsync(int groupId, IEnumerable<string> emails, bool moveFromOtherGroups, MembershipSource source, GroupActor actor,
        string? serviceNowReference, string? note, CancellationToken ct = default)
    {
        var ticket = string.IsNullOrWhiteSpace(serviceNowReference) ? null : serviceNowReference.Trim()[..Math.Min(64, serviceNowReference.Trim().Length)];
        var text = Clean(note, "The note");

        var g = await access.LoadEditableAsync(groupId, actor, ct);
        var candidates = await access.ClassifyAsync(g, emails, moveFromOtherGroups, ct);

        var userIds = candidates.Where(c => c.UserId is not null).Select(c => c.UserId!.Value).ToList();
        var waiting = await WaitingAsync(g.DashboardId, GroupChangeAction.Add, userIds, ct);

        var results = new List<MemberResult>();
        var items = new List<GroupAddRequestItem>();
        foreach (var c in candidates)
        {
            if (c.Outcome is "Added" or "Moved" && waiting.Contains(c.UserId!.Value))
            {
                results.Add(new(c.Email, "AlreadyWaiting", "Already waiting for the owners' approval in another request for this dashboard."));
                continue;
            }
            if (c.Outcome is "Added" or "Moved")
            {
                results.Add(new(c.Email, "Requested", c.Detail is null ? "Sent for approval." : $"Sent for approval. {c.Detail.Replace("Moved from", "Would move from")}"));
                items.Add(new GroupAddRequestItem { UserId = c.UserId!.Value, Result = c.Detail });
            }
            else results.Add(new(c.Email, c.Outcome, c.Detail));
        }
        if (items.Count == 0) return new AddOutcome("Nothing", null, results);

        var requester = await NameAsync(actor.UserId, ct);
        var request = await PersistAsync(g, actor.UserId, requester, GroupChangeAction.Add, null, null, moveFromOtherGroups, source, ticket, text, items,
            results.Where(r => r.Outcome == "Requested").Select(r => r.Email).ToList(), ct);
        return new AddOutcome("Requested", request.Id, results);
    }

    /// <summary>
    /// Sends what an access rule found to the owners as ONE request (an Add or a Remove, as the rule says). The request
    /// has no human requester: it shows as coming from the rule. The caller has already left out people who are waiting
    /// in another request, and people the owners rejected under this version of the rule.
    /// </summary>
    public async Task<GroupAddRequest> SubmitFromRuleAsync(AccessGroupRule rule, DashboardGroup group, IReadOnlyList<RuleProposal> proposals, string ruleText, CancellationToken ct = default)
    {
        if (proposals.Count == 0) throw new RuleException("Nobody to propose.");
        var items = proposals.Select(p => new GroupAddRequestItem { UserId = p.UserId, Result = p.Detail }).ToList();
        return await PersistAsync(group, null, $"Access rule “{rule.Name}”", rule.Action, rule.Id, rule.Name, rule.MoveFromOtherGroups, MembershipSource.AccessRule, null,
            $"Raised by the access rule “{rule.Name}”: {ruleText}.", items, proposals.Select(p => p.Email).ToList(), ct);
    }

    /// <summary>
    /// Copies the live members of another group into this one, as a request to the owners.
    /// People already in a group of this dashboard are skipped, as with any add.
    /// </summary>
    public async Task<AddOutcome> CopyAsync(int sourceGroupId, int targetGroupId, GroupActor actor, string? serviceNowReference, CancellationToken ct = default)
    {
        if (sourceGroupId == targetGroupId) throw new RuleException("Choose a different group to copy from.");
        if (!await db.DashboardGroups.AnyAsync(g => g.Id == sourceGroupId, ct)) throw new KeyNotFoundException();
        var emails = await db.GroupMembers.Where(m => m.GroupId == sourceGroupId && m.RemovedAtUtc == null).Select(m => m.User.Email).ToListAsync(ct);
        if (emails.Count == 0) throw new RuleException("That group has no members to copy.");
        return await SubmitAsync(targetGroupId, emails, false, MembershipSource.Clone, actor, serviceNowReference, $"Copied from another group ({emails.Count} people).", ct);
    }

    /// <summary>People with a pending item for the same kind of change on this dashboard, so nobody is asked about twice.</summary>
    public async Task<HashSet<int>> WaitingAsync(int dashboardId, GroupChangeAction action, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        (await db.GroupAddRequestItems.AsNoTracking()
            .Where(i => i.Decision == GroupAddItemDecision.Pending && i.Request.Status == GroupAddRequestStatus.Pending
                        && i.Request.DashboardId == dashboardId && i.Request.Action == action && userIds.Contains(i.UserId))
            .Select(i => i.UserId).ToListAsync(ct)).ToHashSet();

    private async Task<GroupAddRequest> PersistAsync(DashboardGroup g, int? requestedBy, string requesterLabel, GroupChangeAction action, int? ruleId, string? ruleName,
        bool move, MembershipSource source, string? ticket, string? text, List<GroupAddRequestItem> items, List<string> emails, CancellationToken ct)
    {
        var request = new GroupAddRequest
        {
            DashboardId = g.DashboardId, GroupId = g.Id, RequestedByUserId = requestedBy, Action = action, RuleId = ruleId, RuleName = ruleName,
            ServiceNowReference = ticket, Note = text, MoveFromOtherGroups = move, Source = source, CreatedAtUtc = Now, Items = items,
        };
        db.GroupAddRequests.Add(request);
        await db.SaveChangesAsync(ct);

        var n = items.Count;
        var ownerIds = Owners(g.Dashboard);
        var adding = action == GroupChangeAction.Add;
        audit.Add(adding ? "group.add-requested" : "group.remove-requested", "DashboardGroup", g.Id, g.DashboardId, new
        {
            group = g.Name, count = n, requestId = request.Id, note = text, rule = ruleName, emails = emails.Take(50).ToList(),
        }, ticket);
        notifications.Notify(ownerIds, "group-add.requested", $"{requesterLabel} wants to {Phrase(action, n, g.Name)} on {g.Dashboard.Name}", text, "/approvals");
        await email.QueueAsync(ownerIds, "group-add.requested", $"{n} {People(n)} to {(adding ? "add" : "remove")} on {g.Dashboard.Name}: your approval is needed", "Approval needed",
            $"<p><strong>{EmailOutboxService.Encode(requesterLabel)}</strong> asked to {(adding ? "add" : "remove")} <strong>{n} {People(n)}</strong> {(adding ? "to" : "from")} the access group <strong>{EmailOutboxService.Encode(g.Name)}</strong> on <strong>{EmailOutboxService.Encode(g.Dashboard.Name)}</strong>.</p>"
            + (text is null ? "" : $"<p>{EmailOutboxService.Encode(text)}</p>")
            + $"<p>Open the request to see who is on the list. You can approve everyone, or untick anyone you don't want to {(adding ? "approve" : "remove")}.</p>",
            "Review the Request", $"{email.Options.UserPortalUrl}/approvals", ct);
        await db.SaveChangesAsync(ct);
        return request;
    }

    /// <summary>
    /// The decision on a request: the people in <paramref name="approvedUserIds"/> are added (or removed, for a Remove
    /// request), everyone else on it is rejected (an empty list rejects the whole request). The owners decide as
    /// themselves; a Super Admin deciding in their place, or an owner deciding a request they made, must give an
    /// <paramref name="overrideReason"/>.
    /// </summary>
    public async Task<GroupAddRequest> DecideAsync(int requestId, IReadOnlyCollection<int> approvedUserIds, string? note, string? overrideReason, GroupActor actor, CancellationToken ct = default)
    {
        var r = await db.GroupAddRequests.Include(x => x.Items).ThenInclude(i => i.User).Include(x => x.Dashboard).Include(x => x.Group)
            .SingleOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new KeyNotFoundException();
        if (r.Status != GroupAddRequestStatus.Pending) throw new RuleException($"This request was already {Describe(r.Status)}.");
        var adding = r.Action == GroupChangeAction.Add;

        var reason = Clean(overrideReason, "The reason");
        var decisionNote = Clean(note, "The note");
        var asOwner = IsOwner(r.Dashboard, actor.UserId) && actor.UserId != r.RequestedByUserId && !r.Dashboard.OwnershipPendingReview;
        if (!asOwner)
        {
            if (!actor.IsSuperAdmin)
                throw new RuleException(IsOwner(r.Dashboard, actor.UserId) && actor.UserId == r.RequestedByUserId
                    ? "You can't decide a request you made yourself."
                    : r.Dashboard.OwnershipPendingReview && IsOwner(r.Dashboard, actor.UserId)
                        ? "This dashboard's owners are being reviewed, so approvals are paused until a Super Admin confirms them."
                        : "Only the dashboard's owners can decide this request.");
            if (reason is null) throw new RuleException("Give a reason for deciding in place of the owners. It is saved in the audit log and the group's history.");
        }

        var pending = r.Items.Where(i => i.Decision == GroupAddItemDecision.Pending).ToList();
        var approveIds = approvedUserIds.Distinct().ToHashSet();
        if (approveIds.Except(pending.Select(i => i.UserId)).Any()) throw new RuleException("Some of the people you ticked aren't on this request.");

        var toApply = pending.Where(i => approveIds.Contains(i.UserId)).ToList();
        var doneEmails = new List<string>();
        if (toApply.Count > 0)
        {
            if (r.Group.Status != GroupStatus.Active)
                throw new RuleException($"{r.Group.Name} isn't active, so people can't be {(adding ? "added to" : "removed from")} it. Ask an admin to activate it, or reject this request.");
            if (adding)
            {
                var results = await access.AddMembersAsync(r.GroupId, toApply.Select(i => i.User.Email), r.MoveFromOtherGroups, r.Source, actor, ct,
                    r.ServiceNowReference, notify: true, addedBy: r.RequestedByUserId, overrideReason: asOwner ? null : reason, requestId: r.Id);
                foreach (var item in toApply)
                {
                    var res = results.Single(x => x.Email.Equals(item.User.Email, StringComparison.OrdinalIgnoreCase));
                    if (res.Outcome is "Added" or "Moved") { item.Decision = GroupAddItemDecision.Approved; item.Result = res.Detail ?? "Added"; doneEmails.Add(item.User.Email); }
                    else { item.Decision = GroupAddItemDecision.Skipped; item.Result = res.Outcome == "AlreadyHere" ? "Already in the group." : res.Detail ?? res.Outcome; }
                }
            }
            else
            {
                foreach (var item in toApply)
                {
                    try
                    {
                        await access.RemoveMemberAsync(r.GroupId, item.UserId, actor, ct, reason: $"Removed by the access rule “{r.RuleName}”, approved", requestId: r.Id,
                            overrideReason: asOwner ? null : reason, notify: true);
                        item.Decision = GroupAddItemDecision.Approved; item.Result = "Removed"; doneEmails.Add(item.User.Email);
                    }
                    catch (KeyNotFoundException) { item.Decision = GroupAddItemDecision.Skipped; item.Result = "No longer in the group."; }
                    catch (RuleException ex) { item.Decision = GroupAddItemDecision.Skipped; item.Result = ex.Message; }
                }
            }
        }
        var rejected = pending.Where(i => !approveIds.Contains(i.UserId)).ToList();
        foreach (var item in rejected) { item.Decision = GroupAddItemDecision.Rejected; item.Result = null; }

        var total = r.Items.Count;
        var approvedCount = r.Items.Count(i => i.Decision == GroupAddItemDecision.Approved);
        r.Status = approvedCount == 0 ? GroupAddRequestStatus.Rejected : approvedCount == total ? GroupAddRequestStatus.Approved : GroupAddRequestStatus.PartlyApproved;
        r.DecidedByUserId = actor.UserId;
        r.DecidedAtUtc = Now;
        r.DecisionNote = decisionNote;
        r.OverrideReason = asOwner ? null : reason;

        var kind = adding ? "add" : "remove";
        audit.Add(r.Status == GroupAddRequestStatus.Rejected ? $"group.{kind}-rejected" : $"group.{kind}-approved", "DashboardGroup", r.GroupId, r.DashboardId, new
        {
            group = r.Group.Name, requestId = r.Id, approved = approvedCount, rejected = total - approvedCount, total, rule = r.RuleName,
            approvedEmails = doneEmails.Take(50).ToList(),
            rejectedEmails = r.Items.Where(i => i.Decision != GroupAddItemDecision.Approved).Select(i => i.User.Email).Take(50).ToList(),
            note = decisionNote, overrideReason = r.OverrideReason,
        }, r.ServiceNowReference);

        if (!asOwner)
        {
            var admin = await NameAsync(actor.UserId, ct);
            var what = r.Status == GroupAddRequestStatus.Rejected ? "rejected" : $"approved {approvedCount} of {total}";
            notifications.Notify(Owners(r.Dashboard), "group-add.overridden", $"{admin} {what} a request for {r.Group.Name} in your place", $"Reason: {r.OverrideReason}", "/approvals");
            await email.QueueAsync(Owners(r.Dashboard), "group-add.overridden", $"A Super Admin decided a request for {r.Dashboard.Name} in your place", "Decided by a Super Admin",
                $"<p><strong>{EmailOutboxService.Encode(admin)}</strong> {what} a request to {kind} people {(adding ? "to" : "from")} <strong>{EmailOutboxService.Encode(r.Group.Name)}</strong> on <strong>{EmailOutboxService.Encode(r.Dashboard.Name)}</strong> instead of you.</p>"
                + $"<p>Reason given: <em>{EmailOutboxService.Encode(r.OverrideReason!)}</em></p>", null, null, ct);
        }

        // The admin who asked is told how it went. A request raised by a rule has no one to tell: the rule's page shows the outcome.
        if (r.RequestedByUserId is { } requestedBy)
        {
            var verdict = r.Status switch { GroupAddRequestStatus.Approved => "approved", GroupAddRequestStatus.PartlyApproved => $"partly approved ({approvedCount} of {total})", _ => "not approved" };
            notifications.Notify([requestedBy], "group-add.decided", $"Your request to {Phrase(r.Action, total, r.Group.Name)} was {verdict}", decisionNote, $"/access-groups/{r.GroupId}");
            await email.QueueAsync([requestedBy], "group-add.decided", $"Request for {r.Dashboard.Name} {verdict}", $"Your request was {verdict}",
                $"<p>Your request to {kind} <strong>{total} {People(total)}</strong> {(adding ? "to" : "from")} <strong>{EmailOutboxService.Encode(r.Group.Name)}</strong> on <strong>{EmailOutboxService.Encode(r.Dashboard.Name)}</strong> was <strong>{verdict}</strong>.</p>"
                + (decisionNote is null ? "" : $"<p>Note from the approver: <em>{EmailOutboxService.Encode(decisionNote)}</em></p>")
                + (r.OverrideReason is null ? "" : "<p>A Super Admin decided this in place of the owners.</p>"),
                "Open the Group", $"{email.Options.AdminPortalUrl}/access-groups/{r.GroupId}", ct);
        }
        await db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>The admin who made a request takes it back before it is decided. A Super Admin can withdraw any request, including a rule's.</summary>
    public async Task CancelAsync(int requestId, GroupActor actor, CancellationToken ct = default)
    {
        var r = await db.GroupAddRequests.Include(x => x.Group).SingleOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new KeyNotFoundException();
        if (r.RequestedByUserId != actor.UserId && !actor.IsSuperAdmin) throw new RuleException("Only the person who made this request, or a Super Admin, can withdraw it.");
        if (r.Status != GroupAddRequestStatus.Pending) throw new RuleException($"This request was already {Describe(r.Status)}.");
        r.Status = GroupAddRequestStatus.Cancelled;
        r.DecidedByUserId = actor.UserId;
        r.DecidedAtUtc = Now;
        audit.Add("group.add-cancelled", "DashboardGroup", r.GroupId, r.DashboardId, new { group = r.Group.Name, requestId = r.Id, action = r.Action.ToString(), rule = r.RuleName }, r.ServiceNowReference);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Requests with their people, and what the viewer can do with each: decide as an owner, decide only with a reason (Super Admin), or just read.</summary>
    public async Task<List<GroupAddRow>> RowsAsync(IQueryable<GroupAddRequest> query, int viewerId, bool viewerIsSuperAdmin, CancellationToken ct = default)
    {
        var rows = await query
            .OrderBy(r => r.Status == GroupAddRequestStatus.Pending ? 0 : 1).ThenByDescending(r => r.CreatedAtUtc)
            .Take(200)
            .Select(r => new
            {
                r.Id, Status = r.Status.ToString(), Action = r.Action.ToString(), r.RuleName, r.CreatedAtUtc, r.Note, r.ServiceNowReference, r.MoveFromOtherGroups, r.DecidedAtUtc, r.DecisionNote, r.OverrideReason,
                DecidedBy = r.DecidedByUserId != null ? db.Users.Where(u => u.Id == r.DecidedByUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
                ById = r.RequestedByUserId, ByEmail = r.RequestedBy != null ? r.RequestedBy.Email : null, ByName = r.RequestedBy != null ? r.RequestedBy.DisplayName : null,
                DashboardId = r.DashboardId, Dashboard = r.Dashboard.Name, r.Dashboard.RlsEnabled, r.Dashboard.OwnershipPendingReview,
                r.Dashboard.PrimaryOwnerId, r.Dashboard.BackupOwnerId,
                PrimaryOwner = r.Dashboard.PrimaryOwner != null ? r.Dashboard.PrimaryOwner.DisplayName ?? r.Dashboard.PrimaryOwner.Email : null,
                BackupOwner = r.Dashboard.BackupOwner != null ? r.Dashboard.BackupOwner.DisplayName ?? r.Dashboard.BackupOwner.Email : null,
                GroupId = r.GroupId, Group = r.Group.Name, r.Group.RlsValue, GroupIsDefault = r.Group.IsDefault, r.RequestedByUserId,
                Items = r.Items.OrderBy(i => i.User.DisplayName ?? i.User.Email).Select(i => new
                {
                    i.UserId, i.User.Email, i.User.DisplayName, UserType = i.User.UserType.ToString(),
                    Title = i.User.HrmsProfile != null ? i.User.HrmsProfile.JobTitle : null,
                    Department = i.User.HrmsProfile != null ? i.User.HrmsProfile.Department : null,
                    Decision = i.Decision.ToString(), i.Result,
                    CurrentGroup = db.GroupMembers.Where(m => m.DashboardId == r.DashboardId && m.UserId == i.UserId && m.RemovedAtUtc == null && m.GroupId != r.GroupId)
                        .Select(m => m.Group.Name).FirstOrDefault(),
                }).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var viewerIsOwner = r.PrimaryOwnerId == viewerId || r.BackupOwnerId == viewerId;
            var asOwner = viewerIsOwner && r.RequestedByUserId != viewerId && !r.OwnershipPendingReview;
            return new GroupAddRow(r.Id, r.Status, r.Action, r.RuleName, r.CreatedAtUtc, r.Note, r.ServiceNowReference, r.MoveFromOtherGroups, r.DecidedAtUtc, r.DecisionNote, r.OverrideReason, r.DecidedBy,
                r.ById is { } by ? new PersonRef(by, r.ByEmail!, r.ByName) : null, r.DashboardId, r.Dashboard, r.RlsEnabled, r.OwnershipPendingReview,
                r.GroupId, r.Group, r.RlsValue, r.GroupIsDefault,
                r.Items.Select(i => new GroupAddItemRow(i.UserId, i.Email, i.DisplayName, i.UserType, i.Title, i.Department, i.Decision, i.Result, i.CurrentGroup)).ToList(),
                new[] { r.PrimaryOwner, r.BackupOwner }.OfType<string>().ToList(),
                CanDecide: asOwner || viewerIsSuperAdmin, NeedsOverride: !asOwner, IsRequester: r.RequestedByUserId == viewerId);
        }).ToList();
    }

    public static bool IsOwner(Dashboard d, int userId) => d.PrimaryOwnerId == userId || d.BackupOwnerId == userId;
    private static IEnumerable<int> Owners(Dashboard d) => new[] { d.PrimaryOwnerId, d.BackupOwnerId }.OfType<int>();
    private static string People(int n) => n == 1 ? "person" : "people";
    private static string Phrase(GroupChangeAction a, int n, string group) => a == GroupChangeAction.Add ? $"add {n} {People(n)} to {group}" : $"remove {n} {People(n)} from {group}";
    private static string Describe(GroupAddRequestStatus s) => s switch { GroupAddRequestStatus.Cancelled => "withdrawn", GroupAddRequestStatus.PartlyApproved => "partly approved", _ => s.ToString().ToLowerInvariant() };

    private async Task<string> NameAsync(int userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName ?? u.Email).SingleAsync(ct);

    private static string? Clean(string? text, string label)
    {
        var t = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (t?.Length > 1000) throw new RuleException($"{label} is up to 1,000 characters.");
        return t;
    }
}
