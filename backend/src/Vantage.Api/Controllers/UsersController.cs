using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Jobs;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

[Route("api/admin/users")]
public sealed class UsersController(CurrentUser current, AppDbContext db, UserService users, JobRunner jobs, OwnerDepartureService departures) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(string? search, string? type, string? status, int? roleId, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.View, ct) is { } denied) return denied;
        pageSize = Math.Clamp(pageSize, 5, 200);
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(u => u.Email.Contains(s) || (u.DisplayName != null && u.DisplayName.Contains(s))
                             || (u.HrmsProfile != null && (u.HrmsProfile.Department!.Contains(s) || u.HrmsProfile.EmployeeId.Contains(s))));
        }
        if (Enum.TryParse<UserType>(type, true, out var t)) q = q.Where(u => u.UserType == t);
        if (Enum.TryParse<UserStatus>(status, true, out var st)) q = q.Where(u => u.Status == st);
        if (roleId is { } rid) q = q.Where(u => u.Roles.Any(r => r.RoleId == rid));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(u => u.DisplayName ?? u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, userType = u.UserType.ToString(), status = u.Status.ToString(), u.StatusSetManually,
                department = u.HrmsProfile != null ? u.HrmsProfile.Department : null,
                jobTitle = u.HrmsProfile != null ? u.HrmsProfile.JobTitle : null,
                roles = u.Roles.Select(r => r.Role.Name).ToList(),
                dashboards = db.GroupMembers.Count(m => m.UserId == u.Id && m.RemovedAtUtc == null),
                u.CreatedAtUtc,
            })
            .ToListAsync(ct);
        return Ok(new { total, page, pageSize, rows });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.View, ct) is { } denied) return denied;
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Email, x.FirstName, x.LastName, x.DisplayName, userType = x.UserType.ToString(), status = x.Status.ToString(),
                x.StatusSetManually, x.ContactNumber, x.TimeZone, x.TableauUserName, x.CreatedAtUtc, x.UpdatedAtUtc, x.LastLoginAtUtc,
                roles = x.Roles.Select(r => new { r.RoleId, r.Role.Name, r.IsAutomatic }).ToList(),
                hrms = x.HrmsProfile == null ? null : new
                {
                    x.HrmsProfile.EmployeeId, x.HrmsProfile.Department, x.HrmsProfile.Division, x.HrmsProfile.JobTitle, x.HrmsProfile.JobGrade,
                    x.HrmsProfile.ManagerEmail, x.HrmsProfile.Location, x.HrmsProfile.EmploymentStatus, x.HrmsProfile.HireDate, x.HrmsProfile.ExitDate,
                    x.HrmsProfile.LastSyncedAtUtc,
                },
            })
            .SingleOrDefaultAsync(ct);
        if (u is null) return NotFound();

        // Access summary: each dashboard the user can open and the group that grants it.
        var access = await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == id && m.RemovedAtUtc == null)
            .Select(m => new
            {
                dashboardId = m.DashboardId, dashboard = m.Group.Dashboard.Name, dashboardStatus = m.Group.Dashboard.Status.ToString(),
                group = m.Group.Name, m.Group.RlsValue, source = m.Source.ToString(), m.AddedAtUtc,
            })
            .OrderBy(a => a.dashboard)
            .ToListAsync(ct);
        // Change history with the ServiceNow ticket each change was made under.
        var idText = id.ToString();
        var history = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "User" && a.EntityId == idText)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Take(15)
            .Select(a => new
            {
                a.Id, a.Action, a.OccurredAtUtc, a.ServiceNowReference,
                actor = a.ActorUserId != null ? db.Users.Where(x => x.Id == a.ActorUserId).Select(x => x.DisplayName ?? x.Email).FirstOrDefault() : null,
            })
            .ToListAsync(ct);
        return Ok(new { user = u, access, history });
    }

    /// <summary>What the HRMS table holds for an email, to preview before adding an internal user.</summary>
    [HttpGet("hrms-lookup")]
    public async Task<IActionResult> HrmsLookup(string email, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.View, ct) is { } denied) return denied;
        var e = UserService.NormaliseEmail(email);
        if (e is null) return Ok(new { valid = false });
        var domain = await users.InternalDomainAsync(ct);
        var isInternal = Rules.IsInternalEmail(e, domain);
        var exists = await db.Users.Where(u => u.Email == e).Select(u => (int?)u.Id).SingleOrDefaultAsync(ct);
        var h = isInternal ? await db.HrmsSource.AsNoTracking().SingleOrDefaultAsync(x => x.Email == e, ct) : null;
        return Ok(new
        {
            valid = true, email = e, userType = isInternal ? "Internal" : "External", existingUserId = exists,
            hrms = h is null ? null : new { h.EmployeeId, h.FirstName, h.LastName, h.DisplayName, h.Department, h.JobTitle, h.Location, h.EmploymentStatus },
        });
    }

    public sealed record CreateBody(string Email, string? FirstName, string? LastName, string? ContactNumber, string? TimeZone,
        string? TableauUserName, string? ServiceNowReference, List<int>? RoleIds);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await RequireSuperAdminAsync("add users", ct) is { } notSuper) return notSuper;
        return await Guard(async () =>
        {
            var u = await users.CreateAsync(new NewUserInput(body.Email, body.FirstName, body.LastName, body.ContactNumber, body.TimeZone,
                body.TableauUserName, body.ServiceNowReference, body.RoleIds ?? []), Current.UserId, ct);
            return Ok(new { u.Id, u.Email, userType = u.UserType.ToString(), status = u.Status.ToString(), fromHrms = u.HrmsProfile is not null });
        });
    }

    public sealed record BulkBody(string Emails, List<int>? RoleIds, string? ServiceNowReference);

    /// <summary>Adds users from pasted emails (one per line, or separated by commas, semicolons or spaces).</summary>
    [HttpPost("bulk")]
    public async Task<IActionResult> Bulk(BulkBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await RequireSuperAdminAsync("add users", ct) is { } notSuper) return notSuper;
        var emails = body.Emails.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (emails.Length == 0) return BadRequest(new { message = "Paste at least one email address." });
        if (emails.Length > 5000) return BadRequest(new { message = "Up to 5,000 emails at a time." });
        return await Guard(async () => Ok(await users.BulkCreateAsync(emails.Select(e => (e, (string?)null)), body.RoleIds ?? [], Current.UserId, ct, body.ServiceNowReference)));
    }

    /// <summary>Adds users from an Excel file with an "Email" column and an optional "Role" column (role name).</summary>
    [HttpPost("bulk-excel"), RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> BulkExcel(IFormFile file, [FromForm] List<int>? roleIds, [FromForm] string? serviceNowReference, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (await RequireSuperAdminAsync("add users", ct) is { } notSuper) return notSuper;
        var rows = new List<(string, string?)>();
        try
        {
            await using var stream = file.OpenReadStream();
            using var book = new XLWorkbook(stream);
            var sheet = book.Worksheets.First();
            var header = sheet.FirstRowUsed();
            if (header is null) return BadRequest(new { message = "The sheet is empty." });
            int? emailCol = null, roleCol = null;
            foreach (var cell in header.CellsUsed())
            {
                var h = cell.GetString().Trim().ToLowerInvariant();
                if (h is "email" or "email address" or "e-mail") emailCol = cell.Address.ColumnNumber;
                if (h is "role" or "role name") roleCol = cell.Address.ColumnNumber;
            }
            if (emailCol is null) return BadRequest(new { message = "The first row needs an 'Email' column heading." });
            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                var email = row.Cell(emailCol.Value).GetString();
                if (string.IsNullOrWhiteSpace(email)) continue;
                rows.Add((email, roleCol is null ? null : row.Cell(roleCol.Value).GetString()));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { message = "That file couldn't be read as an Excel workbook (.xlsx)." });
        }
        if (rows.Count == 0) return BadRequest(new { message = "No email addresses found under the 'Email' heading." });
        if (rows.Count > 5000) return BadRequest(new { message = "Up to 5,000 rows at a time." });
        return await Guard(async () => Ok(await users.BulkCreateAsync(rows, roleIds ?? [], Current.UserId, ct, serviceNowReference)));
    }

    /// <summary>Excel template for bulk upload.</summary>
    [HttpGet("bulk-template")]
    public async Task<IActionResult> Template(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.View, ct) is { } denied) return denied;
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Users");
        sheet.Cell(1, 1).Value = "Email";
        sheet.Cell(1, 2).Value = "Role";
        sheet.Cell(2, 1).Value = "priya.nair@rrd.com";
        sheet.Cell(3, 1).Value = "jane.doe@clientcompany.com";
        sheet.Cell(3, 2).Value = "Dashboard User";
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents();
        var ms = new MemoryStream();
        book.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "user-upload-template.xlsx");
    }

    public sealed record UpdateBody(string? FirstName, string? LastName, string? DisplayName, string? ContactNumber, string? TimeZone,
        string? TableauUserName, string? ServiceNowReference, UserStatus Status, List<int>? RoleIds);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await users.UpdateAsync(id, new UpdateUserInput(body.FirstName, body.LastName, body.DisplayName, body.ContactNumber, body.TimeZone,
                body.TableauUserName, body.ServiceNowReference, body.Status, body.RoleIds ?? []), Current.UserId, ct);
            // Setting someone Inactive may leave a dashboard with no owner who can act; it then goes Inactive until new owners are named (C43).
            if (body.Status == UserStatus.Inactive) await departures.ReviewAsync(ct);
            return NoContent();
        });
    }

    public sealed record EmailBody(string Email);

    [HttpPut("{id:int}/email")]
    public async Task<IActionResult> ChangeEmail(int id, EmailBody body, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (!me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can change an email address." });
        return await Guard(async () =>
        {
            await users.ChangeEmailAsync(id, body.Email, ct);
            return NoContent();
        });
    }

    /// <summary>Runs the HRMS job now (it also runs on its schedule). The run is recorded on the Scheduled Jobs page like any other.</summary>
    [HttpPost("hrms-sync")]
    public async Task<IActionResult> HrmsSync(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Users, PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = await Current.GetAsync(ct);
        return await Guard(async () =>
        {
            var run = await jobs.RunNowAsync(HrmsSyncJob.JobName, me!.Id, ct);
            if (run.Data is not HrmsJobResult r) return StatusCode(500, new { message = run.Run.Summary ?? "The HRMS sync failed." });
            var s = r.Sync;
            return Ok(new { s.Checked, s.ProfilesUpdated, s.Deactivated, s.SkippedManual, s.NotInHrms, s.DeactivatedEmails, r.LeaversRevoked, r.MembershipsEnded, rules = r.Rules });
        });
    }
}
