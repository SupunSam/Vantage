using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

internal static class XlsxResult
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>A one-sheet workbook: bold header row, frozen, filterable, with the given column widths.</summary>
    public static byte[] Build(string sheetName, string[] heads, double[] widths, IEnumerable<object?[]> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet(sheetName);
        for (var c = 0; c < heads.Length; c++) sheet.Cell(1, c + 1).Value = heads[c];
        sheet.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                switch (row[c])
                {
                    case null: break;
                    case DateTime dt: sheet.Cell(r, c + 1).Value = dt; sheet.Cell(r, c + 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
                    case int i: sheet.Cell(r, c + 1).Value = i; break;
                    case bool b: sheet.Cell(r, c + 1).Value = b ? "Yes" : "No"; break;
                    default: sheet.Cell(r, c + 1).Value = row[c]!.ToString(); break;
                }
            }
            r++;
        }
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }
}

/// <summary>Analytics for Super Admins (module "analytics"): usage across every dashboard, and the two access reports.</summary>
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController(CurrentUser current, AnalyticsService analytics, AppDbContext db) : AdminControllerBase(current)
{
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(int days = 30, string? type = null, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await analytics.OverviewAsync(days, await analytics.ScopeAsync(type, null, ct), ct)));
    }

    [HttpGet("dashboards")]
    public async Task<IActionResult> Dashboards(int days = 30, string? type = null, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await analytics.DashboardsAsync(days, await analytics.ScopeAsync(type, null, ct), ct)));
    }

    [HttpGet("dashboards/{id:int}")]
    public async Task<IActionResult> Dashboard(int id, int days = 30, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await analytics.DashboardAsync(id, days, null, ct)));
    }

    [HttpGet("dashboards/export")]
    public async Task<IActionResult> ExportDashboards(int days = 30, string? type = null, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        List<DashboardUsage> rows;
        try { rows = await analytics.DashboardsAsync(days, await analytics.ScopeAsync(type, null, ct), ct); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        var bytes = XlsxResult.Build("Dashboard Usage", ["Dashboard", "Code", "Type", "Status", "Owner", "Backup Owner", $"Views (last {Math.Clamp(days, 1, AnalyticsService.MaxDays)} days)", "Unique Viewers", "Last Viewed (UTC)", "Members", "Flagged Inactive"],
            [34, 16, 12, 12, 26, 26, 18, 14, 20, 10, 14],
            rows.Select(r => new object?[] { r.Name, r.Code, r.Type, r.Status, r.Owner, r.BackupOwner, r.Views, r.Users, r.LastViewedAtUtc, r.Members, r.Flagged }));
        return File(bytes, XlsxResult.ContentType, $"dashboard-usage-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    /// <summary>Dashboards and people to pick from in the access reports.</summary>
    [HttpGet("report-options")]
    public async Task<IActionResult> ReportOptions(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return Ok(new
        {
            dashboards = await db.Dashboards.AsNoTracking().Where(d => d.Status != DashboardStatus.Retired).OrderBy(d => d.Name)
                .Select(d => new { d.Id, d.Name, d.Code, status = d.Status.ToString() }).ToListAsync(ct),
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(string? q, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        var term = (q ?? "").Trim();
        if (term.Length < 2) return Ok(Array.Empty<object>());
        return Ok(await db.Users.AsNoTracking().Where(u => u.Email.Contains(term) || (u.DisplayName != null && u.DisplayName.Contains(term)))
            .OrderBy(u => u.DisplayName ?? u.Email).Take(10).Select(u => new { u.Id, u.Email, u.DisplayName, status = u.Status.ToString() }).ToListAsync(ct));
    }

    [HttpGet("reports/who-can-open/{dashboardId:int}")]
    public async Task<IActionResult> WhoCanOpen(int dashboardId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await analytics.WhoCanOpenAsync(dashboardId, ct)));
    }

    [HttpGet("reports/who-can-open/{dashboardId:int}/export")]
    public async Task<IActionResult> ExportWhoCanOpen(int dashboardId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var r = await analytics.WhoCanOpenAsync(dashboardId, ct);
            var bytes = XlsxResult.Build("Who Can Open", ["Person", "Email", "Type", "User Status", "Access Group", "Group Status", "RLS Value", "In Group Since (UTC)", "Can Open Now"],
                [28, 34, 10, 12, 30, 12, 24, 20, 12],
                r.People.Select(p => new object?[] { p.Name ?? p.Email, p.Email, p.UserType, p.UserStatus, p.Group, p.GroupStatus, p.RlsValue, p.AddedAtUtc, p.CanOpenNow }));
            return File(bytes, XlsxResult.ContentType, $"who-can-open-{r.Dashboard.Replace(' ', '-')}.xlsx");
        });
    }

    [HttpGet("reports/what-can-open/{userId:int}")]
    public async Task<IActionResult> WhatCanOpen(int userId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await analytics.WhatCanOpenAsync(userId, ct)));
    }

    [HttpGet("reports/what-can-open/{userId:int}/export")]
    public async Task<IActionResult> ExportWhatCanOpen(int userId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Analytics, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var r = await analytics.WhatCanOpenAsync(userId, ct);
            var bytes = XlsxResult.Build("What Can Open", ["Dashboard", "Code", "Type", "Dashboard Status", "Access Group", "Group Status", "RLS Value", "In Group Since (UTC)", "Can Open Now"],
                [34, 16, 12, 14, 30, 12, 24, 20, 12],
                r.Dashboards.Select(d => new object?[] { d.Dashboard, d.Code, d.Type, d.Status, d.Group, d.GroupStatus, d.RlsValue, d.AddedAtUtc, d.CanOpenNow }));
            return File(bytes, XlsxResult.ContentType, $"what-can-open-{r.Email}.xlsx");
        });
    }
}

/// <summary>Analytics for dashboard owners, in the User Portal: usage of the dashboards they own.</summary>
[ApiController, Authorize, Route("api/owner/analytics")]
public sealed class OwnerAnalyticsController(CurrentUser current, AnalyticsService analytics) : ControllerBase
{
    private async Task<(List<int>? Owned, IActionResult? Denied)> OwnedAsync(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return (null, Unauthorized());
        return (await analytics.OwnedAsync(me.Id, ct), null);
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(int days = 30, string? type = null, CancellationToken ct = default)
    {
        var (owned, denied) = await OwnedAsync(ct);
        if (denied is not null) return denied;
        try { return Ok(new { owns = owned!.Count > 0, overview = await analytics.OverviewAsync(days, await analytics.ScopeAsync(type, owned, ct), ct) }); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("dashboards")]
    public async Task<IActionResult> Dashboards(int days = 30, string? type = null, CancellationToken ct = default)
    {
        var (owned, denied) = await OwnedAsync(ct);
        if (denied is not null) return denied;
        try { return Ok(await analytics.DashboardsAsync(days, await analytics.ScopeAsync(type, owned, ct), ct)); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("dashboards/{id:int}")]
    public async Task<IActionResult> Dashboard(int id, int days = 30, CancellationToken ct = default)
    {
        var (owned, denied) = await OwnedAsync(ct);
        if (denied is not null) return denied;
        try { return Ok(await analytics.DashboardAsync(id, days, owned, ct)); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Not found." }); }
    }
}
