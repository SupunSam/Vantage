using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>Audit Log (Admin Portal, module "audit"): search the trail, open one entry, export to Excel. Read only.</summary>
[Route("api/admin/audit-log")]
public sealed class AuditLogController(CurrentUser current, AuditLogService audit) : AdminControllerBase(current)
{
    private static AuditQuery Build(DateOnly? from, DateOnly? to, string? actor, string? entityType, string? action, string? serviceNow, string? search, int? dashboardId, int page = 1, int pageSize = 50)
        => new(from, to, actor, entityType, action, serviceNow, search, dashboardId, page, pageSize);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, string? actor, string? entityType, string? action,
        string? serviceNow, string? search, int? dashboardId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Audit, PermissionLevel.View, ct) is { } denied) return denied;
        return Ok(await audit.SearchAsync(Build(from, to, actor, entityType, action, serviceNow, search, dashboardId, page, pageSize), ct));
    }

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Audit, PermissionLevel.View, ct) is { } denied) return denied;
        var (types, actions) = await audit.OptionsAsync(ct);
        return Ok(new { entityTypes = types, actions });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Audit, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await audit.GetAsync(id, ct)));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, string? actor, string? entityType, string? action,
        string? serviceNow, string? search, int? dashboardId, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Audit, PermissionLevel.View, ct) is { } denied) return denied;
        var (rows, truncated) = await audit.ExportAsync(Build(from, to, actor, entityType, action, serviceNow, search, dashboardId), ct);

        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Audit Log");
        string[] heads = ["When (UTC)", "Who", "Description", "Action", "Details"];
        for (var c = 0; c < heads.Length; c++) sheet.Cell(1, c + 1).Value = heads[c];
        sheet.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < rows.Count; r++)
        {
            var x = rows[r];
            var row = r + 2;
            sheet.Cell(row, 1).Value = x.OccurredAtUtc;
            sheet.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            sheet.Cell(row, 2).Value = x.Actor ?? "System";
            sheet.Cell(row, 3).Value = x.Description;
            sheet.Cell(row, 4).Value = x.Action;
            // Excel cells hold at most 32,767 characters.
            sheet.Cell(row, 5).Value = x.Details is { Length: > 32000 } d ? d[..32000] : x.Details ?? "";
        }
        double[] widths = [20, 28, 90, 26, 80];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        sheet.Column(3).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();

        using var ms = new MemoryStream();
        book.SaveAs(ms);
        if (truncated) Response.Headers["X-Export-Truncated"] = "true";
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }
}
