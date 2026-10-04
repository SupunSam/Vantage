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
public sealed class AccessGroupsController(CurrentUser current, AppDbContext db, AccessGroupService access, GroupService groups, GroupAddRequestService addRequests) : AdminControllerBase(current)
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
            ruleCount = db.AccessGroupRules.Count(r => r.GroupId == x.Id),
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

        var pendingRequests = await db.GroupAddRequests.AsNoTracking()
            .Where(r => r.GroupId == id && r.Status == GroupAddRequestStatus.Pending)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new
            {
                r.Id, r.CreatedAtUtc, r.ServiceNowReference, r.Note, requestedById = r.RequestedByUserId, action = r.Action.ToString(),
                requestedBy = r.RequestedBy != null ? r.RequestedBy.DisplayName ?? r.RequestedBy.Email : "Access rule “" + r.RuleName + "”",
                count = r.Items.Count(i => i.Decision == GroupAddItemDecision.Pending),
                people = r.Items.OrderBy(i => i.User.DisplayName ?? i.User.Email).Take(3).Select(i => i.User.DisplayName ?? i.User.Email).ToList(),
            })
            .ToListAsync(ct);

        return Ok(new
        {
            group = g,
            pendingRequests,
            myId = me.Id,
            canEdit = me.Can(AppModules.Groups, PermissionLevel.Edit),
            isSuperAdmin = me.IsSuperAdmin,
            noRlsMessage = GroupService.NoRlsMessage,
        });
    }

    /// <summary>
    /// The group's history, newest first, a page at a time. Each line is a plain sentence built from the audit log
    /// (the same wording as the Audit Log page), so rules, requests and overrides all read the same way.
    /// </summary>
    [HttpGet("{id:int}/history")]
    public async Task<IActionResult> History(int id, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.View, ct) is { } denied) return denied;
        var info = await db.DashboardGroups.AsNoTracking().Where(g => g.Id == id).Select(g => new { g.Name, Dashboard = g.Dashboard.Name }).SingleOrDefaultAsync(ct);
        if (info is null) return NotFound();
        var size = Math.Clamp(pageSize, 5, 100);
        var idText = id.ToString();
        var query = db.AuditLogs.AsNoTracking().Where(a => a.EntityType == "DashboardGroup" && a.EntityId == idText);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id)
            .Skip((Math.Max(1, page) - 1) * size).Take(size)
            .Select(a => new
            {
                a.Id, a.Action, a.Details, a.OccurredAtUtc, a.ServiceNowReference,
                actor = a.ActorUserId != null ? db.Users.Where(u => u.Id == a.ActorUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
            }).ToListAsync(ct);
        return Ok(new
        {
            total, page = Math.Max(1, page), pageSize = size,
            rows = rows.Select(a => new
            {
                a.Id, a.OccurredAtUtc, a.actor, a.ServiceNowReference,
                // On the group's own page the group is obvious, so the sentences say "this group".
                description = AuditDescriber.Describe(a.Action, "DashboardGroup", idText, a.Details, info.Name, null, null).Replace($"group {info.Name}", "this group"),
            }),
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
            // The group itself is created now; the people copied into it go to the owners for approval like any other add.
            var (g, _) = await access.CreateAsync(body.DashboardId, body.Name, body.RlsValue, body.CopyFromGroupId, actor, ct, copyMembers: false);
            AddOutcome? outcome = null;
            if (body.CopyFromGroupId is { } src && await db.GroupMembers.AnyAsync(m => m.GroupId == src && m.RemovedAtUtc == null, ct))
                outcome = await addRequests.CopyAsync(src, g.Id, actor, null, ct);
            return Ok(new { g.Id, g.Name, copied = outcome?.Results ?? [], mode = outcome?.Mode, requestId = outcome?.RequestId });
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

    public sealed record MembersBody(string Emails, bool MoveFromOtherGroups, string? ServiceNowReference, string? Note);

    /// <summary>Adds people from pasted emails (one per line, or separated by commas, semicolons or spaces), or a single email.</summary>
    [HttpPost("{id:int}/members")]
    public async Task<IActionResult> AddMembers(int id, MembersBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        var emails = SplitEmails(body.Emails);
        var source = emails.Count == 1 ? MembershipSource.Manual : MembershipSource.BulkUpload;
        return await Guard(async () => Ok(await addRequests.SubmitAsync(id, emails, body.MoveFromOtherGroups, source, actor, body.ServiceNowReference, body.Note, ct)));
    }

    /// <summary>Adds people from an Excel file: emails under an "Email" heading (or in the first column).</summary>
    [HttpPost("{id:int}/members/excel"), RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> AddMembersExcel(int id, IFormFile file, [FromForm] bool moveFromOtherGroups, [FromForm] string? serviceNowReference, [FromForm] string? note, CancellationToken ct)
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
        return await Guard(async () => Ok(await addRequests.SubmitAsync(id, emails, moveFromOtherGroups, MembershipSource.BulkUpload, actor, serviceNowReference, note, ct)));
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

    public sealed record CopyBody(int SourceGroupId, string? ServiceNowReference);

    [HttpPost("{id:int}/copy-members")]
    public async Task<IActionResult> CopyMembers(int id, CopyBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        var actor = await ActorAsync(ct);
        return await Guard(async () => Ok(await addRequests.CopyAsync(body.SourceGroupId, id, actor, body.ServiceNowReference, ct)));
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
