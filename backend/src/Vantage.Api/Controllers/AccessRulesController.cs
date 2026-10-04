using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>
/// Access Group Rules (Admin Portal): "people whose HRMS field equals a value are proposed for adding to, or removing
/// from, an access group". Anyone with Access Groups permission can read them; only Super Admins create, change or run
/// them. A rule never changes membership: what it finds goes to the owners as a request.
/// </summary>
[Route("api/admin/access-rules")]
public sealed class AccessRulesController(CurrentUser current, AppDbContext db, AccessGroupRuleService rules) : AdminControllerBase(current)
{
    public sealed record ConditionBody(string Field, string Value);
    public sealed record RuleBody(int GroupId, string Name, string Action, bool MoveFromOtherGroups, bool IsActive, List<ConditionBody>? Conditions);

    private static RuleInput ToInput(RuleBody b) => new(
        b.GroupId, b.Name, Enum.TryParse<GroupChangeAction>(b.Action, true, out var a) ? a : throw new RuleException("Choose whether the rule adds or removes people."),
        b.MoveFromOtherGroups, b.IsActive, (b.Conditions ?? []).Select(c => new RuleCondition(c.Field, c.Value)).ToList());

    private async Task<GroupActor?> SuperAdminAsync(CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        return me is { IsSuperAdmin: true } ? new GroupActor(me.Id, true) : null;
    }

    private IActionResult OnlySuperAdmins() => StatusCode(403, new { message = "Only Super Admins can create, change or run access group rules." });

    [HttpGet]
    public async Task<IActionResult> List(int? groupId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var rows = await db.AccessGroupRules.AsNoTracking()
            .Where(r => groupId == null || r.GroupId == groupId)
            .OrderBy(r => r.Group.Dashboard.Name).ThenBy(r => r.Group.Name).ThenBy(r => r.Name)
            .Select(r => new
            {
                r.Id, r.Name, action = r.Action.ToString(), r.MoveFromOtherGroups, r.IsActive, r.LastRunAtUtc, r.LastRunSummary, r.CreatedAtUtc,
                groupId = r.GroupId, group = r.Group.Name, groupStatus = r.Group.Status.ToString(),
                dashboardId = r.Group.DashboardId, dashboard = r.Group.Dashboard.Name,
                conditions = r.Conditions.OrderBy(c => c.Field).Select(c => new { c.Field, c.Value }).ToList(),
                waiting = db.GroupAddRequests.Count(q => q.RuleId == r.Id && q.Status == GroupAddRequestStatus.Pending),
            }).ToListAsync(ct);
        return Ok(new
        {
            canEdit = me.IsSuperAdmin,
            fields = AccessRuleFields.All.Select(f => new { key = f.Key, label = f.Label }),
            rules = rows.Select(r => new
            {
                r.Id, r.Name, r.action, r.MoveFromOtherGroups, r.IsActive, r.LastRunAtUtc, r.LastRunSummary, r.CreatedAtUtc, r.groupId, r.group, r.groupStatus, r.dashboardId, r.dashboard, r.waiting,
                r.conditions,
                text = AccessGroupRuleService.Text(r.conditions.Select(c => new RuleCondition(c.Field, c.Value)).ToList()),
            }),
        });
    }

    /// <summary>The values that exist in the HRMS data for each field, to suggest while writing a rule.</summary>
    [HttpGet("field-values")]
    public async Task<IActionResult> FieldValues(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        return Ok(await rules.FieldValuesAsync(ct));
    }

    /// <summary>What a draft rule would do right now. Nothing is saved or sent.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(RuleBody body, int? ruleId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await rules.PreviewAsync(ToInput(body), ruleId, ct)));
    }

    [HttpPost]
    public async Task<IActionResult> Create(RuleBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is not { } actor) return OnlySuperAdmins();
        return await Guard(async () => { var r = await rules.CreateAsync(ToInput(body), actor, ct); return Ok(new { r.Id }); });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, RuleBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is not { } actor) return OnlySuperAdmins();
        return await Guard(async () => { await rules.UpdateAsync(id, ToInput(body), actor, ct); return NoContent(); });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is not { } actor) return OnlySuperAdmins();
        return await Guard(async () => { await rules.DeleteAsync(id, actor, ct); return NoContent(); });
    }

    public sealed record ActiveBody(bool Active);

    [HttpPost("{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, ActiveBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is not { } actor) return OnlySuperAdmins();
        return await Guard(async () => { await rules.SetActiveAsync(id, body.Active, actor, ct); return NoContent(); });
    }

    /// <summary>Runs one rule now: whatever it finds goes to the owners for approval.</summary>
    [HttpPost("{id:int}/run")]
    public async Task<IActionResult> Run(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is null) return OnlySuperAdmins();
        return await Guard(async () => Ok(await rules.RunAsync(id, ct)));
    }

    /// <summary>Runs every active rule now (only those of one group when groupId is given).</summary>
    [HttpPost("run-all")]
    public async Task<IActionResult> RunAll(int? groupId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await SuperAdminAsync(ct) is null) return OnlySuperAdmins();
        return await Guard(async () => Ok(await rules.RunAllAsync(ct, groupId)));
    }
}
