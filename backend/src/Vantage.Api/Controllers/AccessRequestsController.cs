using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Api.Controllers;

/// <summary>
/// User Portal side of access: the catalogue, requesting access, the requester's own requests, and the owner's
/// approvals. Super Admins decide on the owners' behalf from the Admin Portal (AdminAccessRequestsController).
/// </summary>
[ApiController, Authorize, Route("api")]
public sealed class AccessRequestsController(AppDbContext db, CurrentUser current, AccessRequestService requests, CategoryService categories) : ControllerBase
{
    /// <summary>Every active dashboard the user may see, with whether they can open it or have a request pending.</summary>
    [HttpGet("catalogue")]
    public async Task<IActionResult> Catalogue(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        var external = me.UserType == UserType.External;
        var showInternal = !external || string.Equals(
            await db.SystemSettings.Where(s => s.Key == SettingKeys.ExternalSeeInternalCatalogue).Select(s => s.Value).SingleOrDefaultAsync(ct), "true", StringComparison.OrdinalIgnoreCase);

        var rows = await db.Dashboards.AsNoTracking()
            .Where(d => d.Status == DashboardStatus.Active && (showInternal || d.Audience != Audience.Internal)
                        && !db.BiTypes.Any(t => t.Type == d.Type && !t.IsEnabled && t.HideWhenInactive))
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id, d.Name, d.Description, Type = d.Type.ToString(), d.CategoryId, d.ThumbnailKey, d.PublishedAtUtc,
                Owner = d.PrimaryOwner != null ? d.PrimaryOwner.DisplayName ?? d.PrimaryOwner.Email : null,
                Tags = d.Tags.Select(t => t.Tag).ToList(),
                Ownerless = d.OwnershipPendingReview || (d.PrimaryOwnerId == null && d.BackupOwnerId == null),
                HasAccess = db.GroupMembers.Any(m => m.DashboardId == d.Id && m.UserId == me.Id && m.RemovedAtUtc == null && m.Group.Status == GroupStatus.Active),
                PendingRequestId = db.AccessRequests.Where(r => r.DashboardId == d.Id && r.RequesterUserId == me.Id && r.Status == AccessRequestStatus.Pending)
                    .Select(r => (int?)r.Id).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var paths = await categories.PathsAsync(ct);
        return Ok(rows.Select(r => new
        {
            r.Id, r.Name, r.Description, r.Type, r.CategoryId, categoryPath = r.CategoryId is { } c ? paths.GetValueOrDefault(c) : null,
            thumbnail = AdminDashboardsController.ThumbnailVersion(r.ThumbnailKey), r.PublishedAtUtc, r.Owner, r.Tags,
            state = r.HasAccess ? "Open" : r.PendingRequestId != null ? "Requested" : r.Ownerless ? "Ownerless" : "None",
            r.PendingRequestId,
        }));
    }

    public sealed record RequestBody(int DashboardId, string? Comment);

    [HttpPost("access-requests")]
    public async Task<IActionResult> Create(RequestBody body, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (!me.Can(AppModules.Catalogue, PermissionLevel.View)) return StatusCode(403, new { message = "You can't request access." });
        try
        {
            var r = await requests.CreateAsync(body.DashboardId, me.Id, body.Comment, ct);
            return Ok(new { r.Id, status = r.Status.ToString() });
        }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Dashboard not found." }); }
    }

    /// <summary>The signed-in user's own requests, newest first.</summary>
    [HttpGet("access-requests/mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        var rows = await db.AccessRequests.AsNoTracking().Where(r => r.RequesterUserId == me.Id)
            .OrderByDescending(r => r.RequestedAtUtc).Take(50)
            .Select(r => new
            {
                r.Id, status = r.Status.ToString(), r.RequestedAtUtc, r.DecidedAtUtc, r.RequesterComment, r.DecisionNote,
                dashboardId = r.DashboardId, dashboard = r.Dashboard.Name,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("access-requests/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        try { await requests.CancelAsync(id, me.Id, ct); return NoContent(); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    /// <summary>Requests for the dashboards the signed-in user owns: pending ones, and the last 30 days of decisions.</summary>
    [HttpGet("access-requests/to-approve")]
    public async Task<IActionResult> ToApprove(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        var since = DateTime.UtcNow.AddDays(-30);
        var query = db.AccessRequests.AsNoTracking()
            .Where(r => (r.Dashboard.PrimaryOwnerId == me.Id || r.Dashboard.BackupOwnerId == me.Id)
                        && (r.Status == AccessRequestStatus.Pending || r.DecidedAtUtc >= since));
        return Ok(await RequestRows(db, query, me.Id, ct));
    }

    public sealed record DecisionBody(int? GroupId, string? Note, string? OverrideReason);

    [HttpPost("access-requests/{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, DecisionBody body, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        try { await requests.ApproveAsync(id, body.GroupId, body.Note, new GroupActor(me.Id, me.IsSuperAdmin), ct, body.OverrideReason); return NoContent(); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("access-requests/{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, DecisionBody body, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        try { await requests.RejectAsync(id, body.Note, new GroupActor(me.Id, me.IsSuperAdmin), ct, body.OverrideReason); return NoContent(); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    /// <summary>Request rows with what an approver needs: requester details and the dashboard's groups to choose from.</summary>
    internal static async Task<object> RequestRows(AppDbContext db, IQueryable<AccessRequest> query, int viewerId, CancellationToken ct)
    {
        var rows = await query
            .OrderBy(r => r.Status == AccessRequestStatus.Pending ? 0 : 1).ThenByDescending(r => r.RequestedAtUtc)
            .Take(300)
            .Select(r => new
            {
                r.Id, status = r.Status.ToString(), r.RequestedAtUtc, comment = r.RequesterComment, r.DecidedAtUtc, r.DecisionNote, r.OverrideReason,
                needsOverride = !((r.Dashboard.PrimaryOwnerId == viewerId || r.Dashboard.BackupOwnerId == viewerId) && !r.Dashboard.OwnershipPendingReview),
                owners = new[] { r.Dashboard.PrimaryOwner != null ? r.Dashboard.PrimaryOwner.DisplayName ?? r.Dashboard.PrimaryOwner.Email : null, r.Dashboard.BackupOwner != null ? r.Dashboard.BackupOwner.DisplayName ?? r.Dashboard.BackupOwner.Email : null },
                decidedBy = r.DecidedByUserId != null ? db.Users.Where(u => u.Id == r.DecidedByUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
                assignedGroup = r.AssignedGroup != null ? r.AssignedGroup.Name : null,
                dashboard = new
                {
                    r.Dashboard.Id, r.Dashboard.Name, r.Dashboard.RlsEnabled, r.Dashboard.OwnershipPendingReview,
                    owner = r.Dashboard.PrimaryOwner != null ? r.Dashboard.PrimaryOwner.DisplayName ?? r.Dashboard.PrimaryOwner.Email : null,
                },
                requester = new
                {
                    r.Requester.Id, r.Requester.Email, r.Requester.DisplayName, userType = r.Requester.UserType.ToString(),
                    title = r.Requester.HrmsProfile != null ? r.Requester.HrmsProfile.JobTitle : null,
                    department = r.Requester.HrmsProfile != null ? r.Requester.HrmsProfile.Department : null,
                },
                currentGroup = db.GroupMembers.Where(m => m.DashboardId == r.DashboardId && m.UserId == r.RequesterUserId && m.RemovedAtUtc == null)
                    .Select(m => m.Group.Name + (m.Group.Status != GroupStatus.Active ? " (inactive)" : "")).FirstOrDefault(),
                groups = r.Status == AccessRequestStatus.Pending
                    ? r.Dashboard.Groups.Where(g => g.Status == GroupStatus.Active).OrderByDescending(g => g.IsDefault).ThenBy(g => g.Name)
                        .Select(g => new { g.Id, g.Name, g.RlsValue, g.IsDefault, members = g.Members.Count(m => m.RemovedAtUtc == null) }).ToList()
                    : null,
            })
            .ToListAsync(ct);
        return rows;
    }
}

/// <summary>Admin Portal: every access request. Anyone with Access Groups permission can see them; Super Admins decide on the owners' behalf.</summary>
[Route("api/admin/access-requests")]
public sealed class AdminAccessRequestsController(CurrentUser current, AppDbContext db) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(string? status, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var query = db.AccessRequests.AsNoTracking();
        if (Enum.TryParse<AccessRequestStatus>(status, true, out var s)) query = query.Where(r => r.Status == s);
        var pending = await db.AccessRequests.CountAsync(r => r.Status == AccessRequestStatus.Pending, ct);
        return Ok(new { pending, canDecide = me.IsSuperAdmin, rows = await AccessRequestsController.RequestRows(db, query, me.Id, ct) });
    }
}
