using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>
/// Requests to add people to access groups (C26), seen by the dashboards' owners in the User Portal and decided there.
/// Owners hold approve and reject only. Super Admins can decide too, but only with a recorded reason.
/// </summary>
[ApiController, Authorize, Route("api/group-add-requests")]
public sealed class GroupAddRequestsController(AppDbContext db, CurrentUser current, GroupAddRequestService requests) : ControllerBase
{
    /// <summary>Requests for the dashboards the signed-in user owns: pending ones and the last 30 days of decisions.</summary>
    [HttpGet("to-approve")]
    public async Task<IActionResult> ToApprove(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        var since = DateTime.UtcNow.AddDays(-30);
        var query = db.GroupAddRequests.AsNoTracking()
            .Where(r => (r.Dashboard.PrimaryOwnerId == me.Id || r.Dashboard.BackupOwnerId == me.Id)
                        && (r.Status == GroupAddRequestStatus.Pending || r.DecidedAtUtc >= since));
        return Ok(await requests.RowsAsync(query, me.Id, me.IsSuperAdmin, ct));
    }

    /// <param name="UserIds">The people to add. Anyone on the request who isn't listed is rejected.</param>
    public sealed record ApproveBody(List<int>? UserIds, string? Note, string? OverrideReason);

    [HttpPost("{id:int}/approve")]
    public Task<IActionResult> Approve(int id, ApproveBody body, CancellationToken ct) => Run(me => requests.DecideAsync(id, body.UserIds ?? [], body.Note, body.OverrideReason, me, ct));

    public sealed record RejectBody(string? Note, string? OverrideReason);

    [HttpPost("{id:int}/reject")]
    public Task<IActionResult> Reject(int id, RejectBody body, CancellationToken ct) => Run(me => requests.DecideAsync(id, [], body.Note, body.OverrideReason, me, ct));

    [HttpPost("{id:int}/cancel")]
    public Task<IActionResult> Cancel(int id, CancellationToken ct) => Run(me => requests.CancelAsync(id, me, ct));

    private async Task<IActionResult> Run(Func<GroupActor, Task> action)
    {
        var me = await current.GetAsync(HttpContext.RequestAborted);
        if (me is null) return Unauthorized();
        try { await action(new GroupActor(me.Id, me.IsSuperAdmin)); return NoContent(); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Not found." }); }
    }
}

/// <summary>Admin Portal: every request to add people to a group. Anyone with Access Groups permission can read; Super Admins can decide with a reason.</summary>
[Route("api/admin/group-add-requests")]
public sealed class AdminGroupAddRequestsController(CurrentUser current, AppDbContext db, GroupAddRequestService requests) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(string? status, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var query = db.GroupAddRequests.AsNoTracking();
        if (string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == GroupAddRequestStatus.Approved || r.Status == GroupAddRequestStatus.PartlyApproved);
        else if (Enum.TryParse<GroupAddRequestStatus>(status, true, out var s)) query = query.Where(r => r.Status == s);
        var pending = await db.GroupAddRequests.CountAsync(r => r.Status == GroupAddRequestStatus.Pending, ct);
        return Ok(new { pending, rows = await requests.RowsAsync(query, me.Id, me.IsSuperAdmin, ct) });
    }
}
