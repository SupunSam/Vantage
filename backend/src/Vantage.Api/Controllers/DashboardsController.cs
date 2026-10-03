using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Storage;

namespace Vantage.Api.Controllers;

[ApiController, Authorize, Route("api/dashboards")]
public sealed class DashboardsController(
    AppDbContext db, CurrentUser current, EmbedService embed, CategoryService categories, IFileStore files, TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Dashboards the user can open (active dashboards where they have a live membership of an active group), for the
    /// User Portal home page: newest first, with category path, tags, thumbnail token and pin state.
    /// </summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();

        var rows = await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == me.Id && m.RemovedAtUtc == null && m.Group.Status == GroupStatus.Active
                        && m.Group.Dashboard.Status == DashboardStatus.Active)
            .Select(m => new
            {
                m.Group.Dashboard.Id, m.Group.Dashboard.Code, m.Group.Dashboard.Name, m.Group.Dashboard.Description,
                Type = m.Group.Dashboard.Type.ToString(),
                CategoryId = m.Group.Dashboard.CategoryId,
                Owner = m.Group.Dashboard.PrimaryOwner != null ? m.Group.Dashboard.PrimaryOwner.DisplayName : null,
                Tags = m.Group.Dashboard.Tags.Select(t => t.Tag).ToList(),
                m.Group.Dashboard.ThumbnailKey,
                m.Group.Dashboard.PublishedAtUtc,
                GrantedAtUtc = m.AddedAtUtc,
                Linked = m.Group.Dashboard.PowerBiReportId != null || m.Group.Dashboard.TableauViewUrl != null || m.Group.Dashboard.Type == DashboardType.GenAi,
                PinOrder = db.Pins.Where(p => p.UserId == me.Id && p.DashboardId == m.DashboardId).Select(p => (int?)p.SortOrder).FirstOrDefault(),
                LastViewedAtUtc = db.DashboardViews.Where(v => v.UserId == me.Id && v.DashboardId == m.DashboardId).Max(v => (DateTime?)v.ViewedAtUtc),
            })
            .OrderByDescending(x => x.PublishedAtUtc)
            .ToListAsync(ct);

        var paths = await categories.PathsAsync(ct);
        return Ok(rows.Select(r => new
        {
            r.Id, r.Code, r.Name, r.Description, r.Type, r.CategoryId,
            categoryPath = r.CategoryId is { } c ? paths.GetValueOrDefault(c) : null,
            r.Owner, r.Tags, thumbnail = AdminDashboardsController.ThumbnailVersion(r.ThumbnailKey),
            r.PublishedAtUtc, r.GrantedAtUtc, r.Linked, pinned = r.PinOrder != null, r.PinOrder, r.LastViewedAtUtc,
        }));
    }

    /// <summary>
    /// The dashboard's thumbnail image. Any signed-in user may load it (the catalogue shows every active dashboard).
    /// The ?v= token in the URL changes with the image, so it can be cached.
    /// </summary>
    [HttpGet("{id:int}/thumbnail")]
    public async Task<IActionResult> Thumbnail(int id, CancellationToken ct)
    {
        if (await current.GetAsync(ct) is null) return Unauthorized();
        var key = await db.Dashboards.Where(d => d.Id == id).Select(d => d.ThumbnailKey).SingleOrDefaultAsync(ct);
        if (key is null) return NotFound();
        try
        {
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(files.OpenRead(key), Thumbnails.ContentTypeFor(key));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Pins a dashboard to the top of the home page (up to 12). Only dashboards the user can open.</summary>
    [HttpPost("{id:int}/pin")]
    public async Task<IActionResult> Pin(int id, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (await db.Pins.AnyAsync(p => p.UserId == me.Id && p.DashboardId == id, ct)) return NoContent();
        var canOpen = await db.GroupMembers.AnyAsync(m => m.UserId == me.Id && m.DashboardId == id && m.RemovedAtUtc == null
            && m.Group.Status == GroupStatus.Active && m.Group.Dashboard.Status == DashboardStatus.Active, ct);
        if (!canOpen) return NotFound();
        var count = await db.Pins.CountAsync(p => p.UserId == me.Id, ct);
        if (count >= Rules.PinsPerUserMax)
            return BadRequest(new { message = $"You can pin up to {Rules.PinsPerUserMax} dashboards. Unpin one first." });
        var order = (await db.Pins.Where(p => p.UserId == me.Id).MaxAsync(p => (int?)p.SortOrder, ct) ?? 0) + 1;
        db.Pins.Add(new Pin { UserId = me.Id, DashboardId = id, SortOrder = order, PinnedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/pin")]
    public async Task<IActionResult> Unpin(int id, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        await db.Pins.Where(p => p.UserId == me.Id && p.DashboardId == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Embed details for one dashboard. 403 with reason "no-group" means the user should see the
    /// "no access" page with Request Access; the viewer never receives a token it may not use.
    /// </summary>
    [HttpGet("{id:int}/embed")]
    public async Task<IActionResult> Embed(int id, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        try
        {
            return Ok(await embed.GetEmbedAsync(id, me.Id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (NoAccessException ex)
        {
            // Enough for the "no access" page to offer Request Access, or say a request is already waiting.
            var info = await db.Dashboards.Where(d => d.Id == id).Select(d => new
            {
                d.Name, Ownerless = d.OwnershipPendingReview || (d.PrimaryOwnerId == null && d.BackupOwnerId == null),
                Pending = db.AccessRequests.Any(r => r.DashboardId == id && r.RequesterUserId == me.Id && r.Status == AccessRequestStatus.Pending),
            }).SingleOrDefaultAsync(ct);
            return StatusCode(403, new { code = ex.Reason, dashboardId = id, name = info?.Name, ownerless = info?.Ownerless ?? false, requestPending = info?.Pending ?? false });
        }
        catch (EmbedException ex)
        {
            var name = await db.Dashboards.Where(d => d.Id == id).Select(d => d.Name).SingleOrDefaultAsync(ct);
            return StatusCode(502, new { code = ex.Code, message = ex.Message, name, correlationId = HttpContext.TraceIdentifier });
        }
    }
}
