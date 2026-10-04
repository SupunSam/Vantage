using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>The HRMS fields a rule can test. The keys are stored in the rule's conditions, so never rename one.</summary>
public static class AccessRuleFields
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("Department", "Department"), ("Division", "Division"), ("JobTitle", "Job Title"), ("JobGrade", "Job Grade"),
        ("Location", "Location"), ("ManagerEmail", "Manager Email"), ("EmploymentStatus", "Employment Status"),
    ];

    public static string? Label(string key) => All.FirstOrDefault(f => f.Key == key).Label;
}

public sealed record RuleCondition(string Field, string Value);
public sealed record RuleInput(int GroupId, string Name, GroupChangeAction Action, bool MoveFromOtherGroups, bool IsActive, IReadOnlyList<RuleCondition> Conditions);

/// <param name="Detail">Why the person is in this list: what would happen to them, or why they are left out.</param>
public sealed record RulePerson(string Email, string? Name, string? Detail);

/// <summary>What a rule would do right now: how many people match, who would go to the owners, and who is left out and why.</summary>
public sealed record RulePreview(int Matching, List<RulePerson> WouldPropose, List<RulePerson> Skipped);

public sealed record RuleRunResult(int RuleId, string Rule, int Proposed, int? RequestId, string Message);

/// <summary>
/// Access Group Rules (C29): a Super Admin says "people whose HRMS Department equals Finance should be in this group"
/// (or should come out of it). A rule never changes membership itself. Running it sends what it finds to the dashboard's
/// owners as one request, an Add or a Remove, and the owners confirm who really is added or removed.
/// Rules run when a Super Admin runs them, and after each HRMS sync. Only existing, active portal users with an HRMS
/// profile are considered; people already waiting in a request, and people the owners rejected under the current
/// version of the rule, are not proposed again; the dashboard's owners are never proposed for removal.
/// </summary>
public sealed class AccessGroupRuleService(AppDbContext db, AccessGroupService access, GroupAddRequestService requests, AuditWriter audit, TimeProvider clock)
{
    public const int MaxConditions = 5;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------ Managing rules (Super Admins)

    public async Task<AccessGroupRule> CreateAsync(RuleInput input, GroupActor actor, CancellationToken ct = default)
    {
        RequireSuperAdmin(actor);
        var group = await LoadGroupAsync(input.GroupId, ct);
        var conditions = Validate(input);
        await EnsureNameFreeAsync(input.GroupId, input.Name.Trim(), null, ct);

        var rule = new AccessGroupRule
        {
            GroupId = group.Id, Name = input.Name.Trim(), Action = input.Action, MoveFromOtherGroups = input.Action == GroupChangeAction.Add && input.MoveFromOtherGroups,
            IsActive = input.IsActive, CreatedByUserId = actor.UserId, CreatedAtUtc = Now, UpdatedAtUtc = Now,
            Conditions = conditions.Select(c => new AccessGroupRuleCondition { Field = c.Field, Value = c.Value }).ToList(),
        };
        db.AccessGroupRules.Add(rule);
        await db.SaveChangesAsync(ct);
        audit.Add("group.rule-created", "DashboardGroup", group.Id, group.DashboardId, new { rule = rule.Name, action = rule.Action.ToString(), when = Text(conditions), active = rule.IsActive });
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<AccessGroupRule> UpdateAsync(int ruleId, RuleInput input, GroupActor actor, CancellationToken ct = default)
    {
        RequireSuperAdmin(actor);
        var rule = await db.AccessGroupRules.Include(r => r.Conditions).Include(r => r.Group).SingleOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new KeyNotFoundException();
        var conditions = Validate(input);
        await EnsureNameFreeAsync(rule.GroupId, input.Name.Trim(), ruleId, ct);

        var move = input.Action == GroupChangeAction.Add && input.MoveFromOtherGroups;
        var changed = rule.Action != input.Action || rule.MoveFromOtherGroups != move || Text(rule.Conditions.Select(c => new RuleCondition(c.Field, c.Value)).ToList()) != Text(conditions);
        db.AccessGroupRuleConditions.RemoveRange(rule.Conditions);
        rule.Conditions = conditions.Select(c => new AccessGroupRuleCondition { Field = c.Field, Value = c.Value }).ToList();
        rule.Name = input.Name.Trim();
        rule.Action = input.Action;
        rule.MoveFromOtherGroups = move;
        rule.IsActive = input.IsActive;
        if (changed) rule.UpdatedAtUtc = Now; // a different rule: people rejected under the old one may be proposed again
        audit.Add("group.rule-updated", "DashboardGroup", rule.GroupId, rule.Group.DashboardId, new { rule = rule.Name, action = rule.Action.ToString(), when = Text(conditions), active = rule.IsActive });
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(int ruleId, GroupActor actor, CancellationToken ct = default)
    {
        RequireSuperAdmin(actor);
        var rule = await db.AccessGroupRules.Include(r => r.Group).SingleOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new KeyNotFoundException();
        audit.Add("group.rule-deleted", "DashboardGroup", rule.GroupId, rule.Group.DashboardId, new { rule = rule.Name });
        db.AccessGroupRules.Remove(rule); // requests it already raised stay, still showing the rule's name
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int ruleId, bool active, GroupActor actor, CancellationToken ct = default)
    {
        RequireSuperAdmin(actor);
        var rule = await db.AccessGroupRules.Include(r => r.Group).SingleOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new KeyNotFoundException();
        if (rule.IsActive == active) return;
        rule.IsActive = active;
        audit.Add(active ? "group.rule-enabled" : "group.rule-disabled", "DashboardGroup", rule.GroupId, rule.Group.DashboardId, new { rule = rule.Name });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------ Seeing what a rule would do

    /// <summary>What a draft rule would do right now, without saving it or raising anything.</summary>
    public async Task<RulePreview> PreviewAsync(RuleInput input, int? ruleId, CancellationToken ct = default)
    {
        var group = await LoadGroupAsync(input.GroupId, ct);
        var conditions = Validate(input, nameRequired: false);
        DateTime? since = null;
        if (ruleId is { } id) since = await db.AccessGroupRules.Where(r => r.Id == id).Select(r => (DateTime?)r.UpdatedAtUtc).SingleOrDefaultAsync(ct);
        var plan = await PlanAsync(group, input.Action, input.Action == GroupChangeAction.Add && input.MoveFromOtherGroups, conditions, ruleId, since, ct);
        return new RulePreview(plan.Matching, plan.Proposals.Select(p => new RulePerson(p.Email, p.Name, p.Detail)).ToList(), plan.Skipped);
    }

    // ------------------------------------------------------------ Running

    public async Task<RuleRunResult> RunAsync(int ruleId, CancellationToken ct = default)
    {
        var rule = await db.AccessGroupRules.Include(r => r.Conditions).Include(r => r.Group).ThenInclude(g => g.Dashboard)
            .SingleOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new KeyNotFoundException();
        if (!rule.IsActive) throw new RuleException("Switch the rule on before running it.");
        return await RunLoadedAsync(rule, ct);
    }

    /// <summary>Runs every active rule (of one group, when given), for example right after an HRMS sync. A rule that can't run is reported, not fatal.</summary>
    public async Task<List<RuleRunResult>> RunAllAsync(CancellationToken ct = default, int? groupId = null)
    {
        var ids = await db.AccessGroupRules.Where(r => r.IsActive && (groupId == null || r.GroupId == groupId)).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
        var results = new List<RuleRunResult>();
        foreach (var id in ids)
        {
            var rule = await db.AccessGroupRules.Include(r => r.Conditions).Include(r => r.Group).ThenInclude(g => g.Dashboard).SingleAsync(r => r.Id == id, ct);
            try { results.Add(await RunLoadedAsync(rule, ct)); }
            catch (RuleException ex) { results.Add(new RuleRunResult(rule.Id, rule.Name, 0, null, ex.Message)); }
        }
        return results;
    }

    private async Task<RuleRunResult> RunLoadedAsync(AccessGroupRule rule, CancellationToken ct)
    {
        var group = rule.Group;
        var d = group.Dashboard;
        string? skip =
            group.Status == GroupStatus.Retired || d.Status == DashboardStatus.Retired ? "Skipped: the dashboard or group is retired."
            : group.Status != GroupStatus.Active ? $"Skipped: {group.Name} is not active."
            : d.Status != DashboardStatus.Active ? "Skipped: the dashboard is not active."
            : d.OwnershipPendingReview ? "Skipped: the dashboard's owners are under review, so approvals are paused."
            : null;

        RuleRunResult result;
        if (skip is not null) result = new RuleRunResult(rule.Id, rule.Name, 0, null, skip);
        else
        {
            var conditions = rule.Conditions.Select(c => new RuleCondition(c.Field, c.Value)).ToList();
            var plan = await PlanAsync(group, rule.Action, rule.MoveFromOtherGroups, conditions, rule.Id, rule.UpdatedAtUtc, ct);
            if (plan.Proposals.Count == 0)
                result = new RuleRunResult(rule.Id, rule.Name, 0, null, plan.Matching == 0 ? "Nobody matches right now." : $"{plan.Matching} match, nothing new to propose.");
            else
            {
                var request = await requests.SubmitFromRuleAsync(rule, group, plan.Proposals.Select(p => new RuleProposal(p.UserId, p.Email, p.Detail)).ToList(), Text(conditions), ct);
                var n = plan.Proposals.Count;
                audit.Add("group.rule-run", "DashboardGroup", group.Id, group.DashboardId, new { rule = rule.Name, action = rule.Action.ToString(), count = n, requestId = request.Id });
                result = new RuleRunResult(rule.Id, rule.Name, n, request.Id,
                    $"Sent {n} {(n == 1 ? "person" : "people")} to the owners to {(rule.Action == GroupChangeAction.Add ? "add" : "remove")}.");
            }
        }
        rule.LastRunAtUtc = Now;
        rule.LastRunSummary = result.Message;
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ------------------------------------------------------------ The plan

    private sealed record PlanItem(int UserId, string Email, string? Name, string? Detail);
    private sealed record Plan(int Matching, List<PlanItem> Proposals, List<RulePerson> Skipped);

    /// <summary>People who match the conditions, sorted into those to propose and those left out (with the reason).</summary>
    private async Task<Plan> PlanAsync(DashboardGroup group, GroupChangeAction action, bool move, IReadOnlyList<RuleCondition> conditions, int? ruleId, DateTime? since, CancellationToken ct)
    {
        var matches = await Matching(conditions).Select(u => new { u.Id, u.Email, u.DisplayName }).OrderBy(u => u.DisplayName ?? u.Email).ToListAsync(ct);
        var proposals = new List<PlanItem>();
        var skipped = new List<RulePerson>();
        if (matches.Count == 0) return new Plan(0, proposals, skipped);

        string NameOf(int id) => matches.First(m => m.Id == id).DisplayName ?? matches.First(m => m.Id == id).Email;
        var candidates = new List<(int UserId, string Email, string? Detail)>();

        if (action == GroupChangeAction.Add)
        {
            foreach (var c in await access.ClassifyAsync(group, matches.Select(m => m.Email), move, ct))
            {
                if (c.Outcome is "Added" or "Moved") candidates.Add((c.UserId!.Value, c.Email, c.Detail));
                else if (c.Outcome != "AlreadyHere") skipped.Add(new RulePerson(c.Email, c.UserId is { } id ? NameOf(id) : null, c.Detail ?? c.Outcome));
            }
        }
        else
        {
            var ids = matches.Select(m => m.Id).ToList();
            var live = (await db.GroupMembers.Where(m => m.GroupId == group.Id && m.RemovedAtUtc == null && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
            foreach (var m in matches.Where(m => live.Contains(m.Id)))
            {
                if (m.Id == group.Dashboard.PrimaryOwnerId || m.Id == group.Dashboard.BackupOwnerId)
                    skipped.Add(new RulePerson(m.Email, m.DisplayName, "Owns the dashboard, so they keep access."));
                else candidates.Add((m.Id, m.Email, null));
            }
        }
        if (candidates.Count == 0) return new Plan(matches.Count, proposals, skipped);

        var candidateIds = candidates.Select(c => c.UserId).ToList();
        var inConflict = await ConflictingAsync(group.Id, action, ruleId, ct);
        var waiting = await requests.WaitingAsync(group.DashboardId, action, candidateIds, ct);
        var rejectedBefore = ruleId is { } rid && since is { } s
            ? (await db.GroupAddRequestItems.Where(i => i.Decision == GroupAddItemDecision.Rejected && i.Request.RuleId == rid && i.Request.Action == action
                    && i.Request.GroupId == group.Id && i.Request.CreatedAtUtc >= s && candidateIds.Contains(i.UserId)).Select(i => i.UserId).ToListAsync(ct)).ToHashSet()
            : [];

        foreach (var c in candidates)
        {
            var name = NameOf(c.UserId);
            if (inConflict.Contains(c.UserId)) skipped.Add(new RulePerson(c.Email, name, "Also matches a rule of this group that does the opposite, so a person decides."));
            else if (rejectedBefore.Contains(c.UserId)) skipped.Add(new RulePerson(c.Email, name, "The owners rejected this earlier. Change the rule to propose them again."));
            else if (waiting.Contains(c.UserId)) skipped.Add(new RulePerson(c.Email, name, "Already waiting for the owners' approval."));
            else proposals.Add(new PlanItem(c.UserId, c.Email, name, c.Detail));
        }
        return new Plan(matches.Count, proposals, skipped);
    }

    /// <summary>People who also match an active rule of the same group that does the opposite, so neither rule acts on them.</summary>
    private async Task<HashSet<int>> ConflictingAsync(int groupId, GroupChangeAction action, int? ruleId, CancellationToken ct)
    {
        var others = await db.AccessGroupRules.Include(r => r.Conditions)
            .Where(r => r.GroupId == groupId && r.IsActive && r.Action != action && r.Id != (ruleId ?? 0)).ToListAsync(ct);
        var ids = new HashSet<int>();
        foreach (var o in others)
            ids.UnionWith(await Matching(o.Conditions.Select(c => new RuleCondition(c.Field, c.Value)).ToList()).Select(u => u.Id).ToListAsync(ct));
        return ids;
    }

    /// <summary>Active portal users with an HRMS profile whose details equal every condition (not case sensitive).</summary>
    private IQueryable<User> Matching(IReadOnlyList<RuleCondition> conditions)
    {
        var q = db.Users.AsNoTracking().Where(u => u.Status != UserStatus.Inactive && u.HrmsProfile != null);
        foreach (var c in conditions)
        {
            var v = c.Value.Trim();
            q = c.Field switch
            {
                "Department" => q.Where(u => u.HrmsProfile!.Department == v),
                "Division" => q.Where(u => u.HrmsProfile!.Division == v),
                "JobTitle" => q.Where(u => u.HrmsProfile!.JobTitle == v),
                "JobGrade" => q.Where(u => u.HrmsProfile!.JobGrade == v),
                "Location" => q.Where(u => u.HrmsProfile!.Location == v),
                "ManagerEmail" => q.Where(u => u.HrmsProfile!.ManagerEmail == v),
                "EmploymentStatus" => q.Where(u => u.HrmsProfile!.EmploymentStatus == v),
                _ => throw new RuleException($"'{c.Field}' is not a field a rule can test."),
            };
        }
        return q;
    }

    /// <summary>The values that exist in the HRMS data for each field, to suggest when writing a rule.</summary>
    public async Task<Dictionary<string, List<string>>> FieldValuesAsync(CancellationToken ct = default)
    {
        const int cap = 300;
        var p = db.HrmsProfiles.AsNoTracking();
        return new Dictionary<string, List<string>>
        {
            ["Department"] = await p.Where(x => x.Department != null).Select(x => x.Department!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["Division"] = await p.Where(x => x.Division != null).Select(x => x.Division!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["JobTitle"] = await p.Where(x => x.JobTitle != null).Select(x => x.JobTitle!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["JobGrade"] = await p.Where(x => x.JobGrade != null).Select(x => x.JobGrade!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["Location"] = await p.Where(x => x.Location != null).Select(x => x.Location!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["ManagerEmail"] = await p.Where(x => x.ManagerEmail != null).Select(x => x.ManagerEmail!).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
            ["EmploymentStatus"] = await p.Select(x => x.EmploymentStatus).Distinct().OrderBy(x => x).Take(cap).ToListAsync(ct),
        };
    }

    // ------------------------------------------------------------ Helpers

    /// <summary>"Department equals Finance and Location equals Colombo".</summary>
    public static string Text(IReadOnlyList<RuleCondition> conditions) =>
        string.Join(" and ", conditions.OrderBy(c => c.Field).Select(c => $"{AccessRuleFields.Label(c.Field) ?? c.Field} equals {c.Value}"));

    private static void RequireSuperAdmin(GroupActor actor)
    {
        if (!actor.IsSuperAdmin) throw new RuleException("Only Super Admins can create or change access group rules.");
    }

    private async Task<DashboardGroup> LoadGroupAsync(int groupId, CancellationToken ct)
    {
        var group = await db.DashboardGroups.Include(g => g.Dashboard).SingleOrDefaultAsync(g => g.Id == groupId, ct) ?? throw new KeyNotFoundException();
        if (group.Status == GroupStatus.Retired || group.Dashboard.Status == DashboardStatus.Retired) throw new RuleException("This group belongs to a retired dashboard.");
        return group;
    }

    private async Task EnsureNameFreeAsync(int groupId, string name, int? exceptId, CancellationToken ct)
    {
        if (await db.AccessGroupRules.AnyAsync(r => r.GroupId == groupId && r.Name == name && r.Id != exceptId, ct))
            throw new RuleException("This group already has a rule with that name.");
    }

    private static List<RuleCondition> Validate(RuleInput input, bool nameRequired = true)
    {
        var name = (input.Name ?? "").Trim();
        if (nameRequired && name.Length == 0) throw new RuleException("Give the rule a name.");
        if (name.Length > 100) throw new RuleException("A rule name can have up to 100 characters.");
        if (input.Conditions is null || input.Conditions.Count == 0) throw new RuleException("A rule needs at least one condition, for example Department equals Finance.");
        if (input.Conditions.Count > MaxConditions) throw new RuleException($"A rule can have up to {MaxConditions} conditions.");
        var clean = new List<RuleCondition>();
        foreach (var c in input.Conditions)
        {
            var field = AccessRuleFields.All.FirstOrDefault(f => string.Equals(f.Key, c.Field?.Trim(), StringComparison.OrdinalIgnoreCase)).Key
                ?? throw new RuleException($"'{c.Field}' is not a field a rule can test.");
            var value = (c.Value ?? "").Trim();
            if (value.Length == 0) throw new RuleException($"Enter a value for {AccessRuleFields.Label(field)}.");
            if (value.Length > 200) throw new RuleException("A condition value can have up to 200 characters.");
            if (clean.Any(x => x.Field == field)) throw new RuleException($"{AccessRuleFields.Label(field)} is used twice. A person has only one {AccessRuleFields.Label(field)}, so make a separate rule for each value.");
            clean.Add(new RuleCondition(field, value));
        }
        return clean;
    }
}
