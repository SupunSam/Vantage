using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>
/// Access Groups (Admin Portal, module "groups"): every dashboard's groups, their members and history.
/// Membership of an active group on an active dashboard is the only way to open a dashboard.
/// </summary>
[Route("api/admin/access-groups")]
public sealed class AccessGroupsController(CurrentUser current, AppDbContext db, AccessGroupService access, GroupService groups) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var rows = await db.DashboardGroups.AsNoTracking()
            .OrderBy(g => g.Dashboard.Name).ThenByDescending(g => g.IsDefault).ThenBy(g => g.Name)
            .Select(g => new
            {
                g.Id, g.Name, g.RlsValue, g.IsDefault, status = g.Status.ToString(), g.CreatedAtUtc,
                dashboardId = g.DashboardId, dashboard = g.Dashboard.Name, dashboardCode = g.Dashboard.Code,
                dashboardStatus = g.Dashboard.Status.ToString(), rlsEnabled = g.Dashboard.RlsEnabled,
                members = g.Members.Count(m => m.RemovedAtUtc == null),
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>Dashboards for the "New access group" form. Only dashboards with RLS can take more groups.</summary>
    [HttpGet("dashboards")]
    public async Task<IActionResult> Dashboards(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var rows = await db.Dashboards.AsNoTracking().Where(d => d.Status != DashboardStatus.Retired).OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Code, d.RlsEnabled, status = d.Status.ToString() }).ToListAsync(ct);
        return Ok(new { dashboards = rows, noRlsMessage = GroupService.NoRlsMessage });
    }

    /// <summary>Portal users matching a name or email, for adding one person (only existing users can join a group).</summary>
    [HttpGet("user-search")]
    public async Task<IActionResult> UserSearch(string q, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var term = (q ?? "").Trim();
        if (term.Length < 2) return Ok(Array.Empty<object>());
        var rows = await db.Users.AsNoTracking()
            .Where(u => u.Email.Contains(term) || (u.DisplayName != null && u.DisplayName.Contains(term)))
            .OrderBy(u => u.DisplayName).Take(10)
            .Select(u => new { u.Id, u.Email, u.DisplayName, userType = u.UserType.ToString(), status = u.Status.ToString() })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var g = await db.DashboardGroups.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Name, x.RlsValue, x.IsDefault, status = x.Status.ToString(), x.CreatedAtUtc, x.ClonedFromGroupId,
            clonedFrom = x.ClonedFromGroupId != null ? db.DashboardGroups.Where(s => s.Id == x.ClonedFromGroupId).Select(s => s.Name).FirstOrDefault() : null,
            createdBy = x.CreatedByUserId != null ? db.Users.Where(u => u.Id == x.CreatedByUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
            dashboard = new
            {
                x.Dashboard.Id, x.Dashboard.Name, x.Dashboard.Code, status = x.Dashboard.Status.ToString(), x.Dashboard.RlsEnabled,
                x.Dashboard.PrimaryOwnerId, x.Dashboard.BackupOwnerId, x.Dashboard.OwnershipPendingReview,
            },
            members = x.Members.Where(m => m.RemovedAtUtc == null).OrderBy(m => m.User.DisplayName ?? m.User.Email).Select(m => new
            {
                m.UserId, m.User.Email, m.User.DisplayName, userType = m.User.UserType.ToString(), userStatus = m.User.Status.ToString(),
                source = m.Source.ToString(), m.AddedAtUtc,
                addedBy = m.AddedByUserId != null ? db.Users.Where(u => u.Id == m.AddedByUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
            }).ToList(),
            siblings = x.Dashboard.Groups.Where(s => s.Id != x.Id && s.Status != GroupStatus.Retired).OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name)
                .Select(s => new { s.Id, s.Name, s.RlsValue, status = s.Status.ToString(), s.IsDefault }).ToList(),
        }).SingleOrDefaultAsync(ct);
        if (g is null) return NotFound();

        var idText = id.ToString();
        var history = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "DashboardGroup" && a.EntityId == idText)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Take(30)
            .Select(a => new
            {
                a.Id, a.Action, a.Details, a.OccurredAtUtc, a.ServiceNowReference,
                actor = a.ActorUserId != null ? db.Users.Where(u => u.Id == a.ActorUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            group = g,
            history,
            canEdit = me.Can(AppModules.Groups, PermissionLevel.Edit),
            isSuperAdmin = me.IsSuperAdmin,
            noRlsMessage = GroupService.NoRlsMessage,
        });
    }

    public sealed record CreateBody(int DashboardId, string Name, string? RlsValue, int? CopyFromGroupId);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () =>
        {
            var (g, copied) = await access.CreateAsync(body.DashboardId, body.Name, body.RlsValue, body.CopyFromGroupId, actor, ct);
            return Ok(new { g.Id, g.Name, copied });
        });
    }

    public sealed record DetailsBody(string? Name, string? RlsValue, bool Active);

    /// <summary>Edits the group's details: name (not the default group's), RLS value and status.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DetailsBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => { await access.UpdateDetailsAsync(id, body.Name, body.RlsValue, body.Active, actor, ct); return NoContent(); });
    }

    public sealed record RenameBody(string Name);

    [HttpPut("{id:int}/name")]
    public async Task<IActionResult> Rename(int id, RenameBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => { await access.RenameAsync(id, body.Name, actor, ct); return NoContent(); });
    }

    public sealed record RlsBody(string? RlsValue);

    [HttpPut("{id:int}/rls")]
    public async Task<IActionResult> SetRls(int id, RlsBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var dashboardId = await db.DashboardGroups.Where(g => g.Id == id).Select(g => (int?)g.DashboardId).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
            var g = await groups.SetRlsAsync(dashboardId, id, body.RlsValue, ct);
            return Ok(new { g.Name, g.RlsValue });
        });
    }

    public sealed record StatusBody(bool Active);

    [HttpPost("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, StatusBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => { await access.SetActiveAsync(id, body.Active, actor, ct); return NoContent(); });
    }

    public sealed record MembersBody(string Emails, bool MoveFromOtherGroups, string? ServiceNowReference);

    /// <summary>Adds people from pasted emails (one per line, or separated by commas, semicolons or spaces), or a single email.</summary>
    [HttpPost("{id:int}/members")]
    public async Task<IActionResult> AddMembers(int id, MembersBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        var emails = SplitEmails(body.Emails);
        var source = emails.Count == 1 ? MembershipSource.Manual : MembershipSource.BulkUpload;
        return await Guard(async () => Ok(await access.AddMembersAsync(id, emails, body.MoveFromOtherGroups, source, actor, ct, body.ServiceNowReference)));
    }

    /// <summary>Adds people from an Excel file: emails under an "Email" heading (or in the first column).</summary>
    [HttpPost("{id:int}/members/excel"), RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> AddMembersExcel(int id, IFormFile file, [FromForm] bool moveFromOtherGroups, [FromForm] string? serviceNowReference, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        List<string> emails;
        try
        {
            await using var stream = file.OpenReadStream();
            emails = ReadEmails(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { message = "That file couldn't be read as an Excel workbook (.xlsx)." });
        }
        if (emails.Count == 0) return BadRequest(new { message = "No email addresses found. Put them under an 'Email' heading in the first sheet." });
        return await Guard(async () => Ok(await access.AddMembersAsync(id, emails, moveFromOtherGroups, MembershipSource.BulkUpload, actor, ct, serviceNowReference)));
    }

    [HttpGet("members-template")]
    public async Task<IActionResult> Template(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Members");
        sheet.Cell(1, 1).Value = "Email";
        sheet.Cell(2, 1).Value = "priya.nair@rrd.com";
        sheet.Cell(3, 1).Value = "jane.doe@clientcompany.com";
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Column(1).Width = 36;
        var ms = new MemoryStream();
        book.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "access-group-members.xlsx");
    }

    [HttpDelete("{id:int}/members/{userId:int}")]
    public async Task<IActionResult> RemoveMember(int id, int userId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => { await access.RemoveMemberAsync(id, userId, actor, ct); return NoContent(); });
    }

    public sealed record MoveBody(int TargetGroupId);

    [HttpPost("{id:int}/members/{userId:int}/move")]
    public async Task<IActionResult> MoveMember(int id, int userId, MoveBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => { await access.MoveMemberAsync(id, userId, body.TargetGroupId, actor, ct); return NoContent(); });
    }

    public sealed record CopyBody(int SourceGroupId);

    [HttpPost("{id:int}/copy-members")]
    public async Task<IActionResult> CopyMembers(int id, CopyBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => Ok(await access.CopyMembersAsync(body.SourceGroupId, id, actor, ct)));
    }

    private async Task<GroupActor> ActorAsync(CancellationToken ct)
    {
        var me = (await Current.GetAsync(ct))!;
        return new GroupActor(me.Id, me.IsSuperAdmin);
    }

    internal static List<string> SplitEmails(string? text) =>
        (text ?? "").Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Emails from the first sheet: the column headed "Email", or the first column when there is no such heading.</summary>
    internal static List<string> ReadEmails(Stream stream)
    {
        using var book = new XLWorkbook(stream);
        var sheet = book.Worksheets.First();
        var header = sheet.FirstRowUsed();
        if (header is null) return [];
        var col = header.CellsUsed().FirstOrDefault(c => c.GetString().Trim().ToLowerInvariant() is "email" or "email address" or "e-mail")?.Address.ColumnNumber;
        var rows = sheet.RowsUsed().Skip(col is null ? 0 : 1);
        return rows.Select(r => r.Cell(col ?? 1).GetString().Trim()).Where(e => e.Contains('@')).ToList();
    }
}
