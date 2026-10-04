using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>
/// Dashboards Master (Admin Portal): the grid of every dashboard, editing details after publishing, the thumbnail,
/// the dashboard's groups, and a preview. Super Admins administer rather than consume: they preview as the members
/// of any group would see it without being in that group, and there is no way to add oneself without approval.
/// </summary>
[Route("api/admin/dashboards")]
public sealed class AdminDashboardsController(
    CurrentUser current, AppDbContext db, CategoryService categories, DashboardMasterService master, GroupService groups,
    DashboardVersionService versions, EmbedService embed) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var paths = await categories.PathsAsync(ct);
        var rows = await db.Dashboards.AsNoTracking()
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new
            {
                d.Id, d.Code, d.Name, d.Description, Type = d.Type.ToString(), Status = d.Status.ToString(), d.RlsEnabled, d.LastError,
                d.CategoryId, Tenant = d.Tenant != null ? d.Tenant.Name : null, Workspace = d.Workspace != null ? d.Workspace.Name : null,
                Owner = d.PrimaryOwner != null ? d.PrimaryOwner.DisplayName ?? d.PrimaryOwner.Email : null,
                BackupOwner = d.BackupOwner != null ? d.BackupOwner.DisplayName ?? d.BackupOwner.Email : null,
                Tags = d.Tags.Select(t => t.Tag).ToList(),
                Audience = d.Audience.ToString(), Classification = d.DataClassification.ToString(),
                d.ThumbnailKey, d.PublishedAtUtc, d.CreatedAtUtc,
                Groups = d.Groups.Count(g => g.Status != GroupStatus.Retired),
                GroupsWithoutRls = d.Groups.Count(g => g.Status == GroupStatus.Active && g.RlsValue == null),
                Members = db.GroupMembers.Count(m => m.DashboardId == d.Id && m.RemovedAtUtc == null),
                MyGroup = db.GroupMembers.Where(m => m.DashboardId == d.Id && m.UserId == me.Id && m.RemovedAtUtc == null).Select(m => m.Group.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return Ok(rows.Select(r => new
        {
            r.Id, r.Code, r.Name, r.Description, r.Type, r.Status, r.RlsEnabled, r.LastError, r.CategoryId,
            categoryPath = r.CategoryId is { } c ? paths.GetValueOrDefault(c) : null,
            r.Tenant, r.Workspace, r.Owner, r.BackupOwner, r.Tags, r.Audience, r.Classification,
            thumbnail = ThumbnailVersion(r.ThumbnailKey), r.PublishedAtUtc, r.CreatedAtUtc, r.Groups,
            needsRls = r.RlsEnabled && r.GroupsWithoutRls > 0, r.Members, r.MyGroup,
        }));
    }

    /// <summary>Users and categories for the owner and category pickers on the edit form.</summary>
    [HttpGet("form-options")]
    public async Task<IActionResult> FormOptions(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.View, ct) is { } denied) return denied;
        var users = await db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active).OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.Email, u.DisplayName }).ToListAsync(ct);
        return Ok(new { users, categories = await categories.ListAsync(ct), limits = Limits });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        var d = await db.Dashboards.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Code, x.Name, x.Description, type = x.Type.ToString(), status = x.Status.ToString(), x.RlsEnabled, x.LastError,
            x.CategoryId, x.PrimaryOwnerId, x.BackupOwnerId,
            tenant = x.Tenant != null ? x.Tenant.Name : null, workspace = x.Workspace != null ? x.Workspace.Name : null,
            owner = x.PrimaryOwner != null ? x.PrimaryOwner.DisplayName : null, backupOwner = x.BackupOwner != null ? x.BackupOwner.DisplayName : null,
            tags = x.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(),
            audience = x.Audience.ToString(), dataClassification = x.DataClassification.ToString(),
            x.SharePointFolder, x.SharePointSubFolder, x.ThumbnailKey,
            x.PowerBiReportId, x.PowerBiDatasetId, x.PublishedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc,
            groups = x.Groups.OrderByDescending(g => g.IsDefault).ThenBy(g => g.Name).Select(g => new
            {
                g.Id, g.Name, g.RlsValue, g.IsDefault, status = g.Status.ToString(),
                members = g.Members.Where(m => m.RemovedAtUtc == null).OrderBy(m => m.User.DisplayName)
                    .Select(m => new { m.User.Email, m.User.DisplayName, source = m.Source.ToString() }).ToList(),
            }).ToList(),
            x.ReplaceVersionNumber,
            versions = x.Versions.OrderByDescending(v => v.VersionNumber).Select(v => new
            {
                v.VersionNumber, v.FileName, v.SizeBytes, v.IsCurrent, v.UploadedAtUtc, note = v.ScanReport,
                uploadedBy = v.UploadedByUserId != null ? db.Users.Where(u => u.Id == v.UploadedByUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() : null,
            }).ToList(),
        }).SingleOrDefaultAsync(ct);
        if (d is null) return NotFound();
        var paths = await categories.PathsAsync(ct);
        return Ok(new
        {
            dashboard = d,
            categoryPath = d.CategoryId is { } c ? paths.GetValueOrDefault(c) : null,
            thumbnail = ThumbnailVersion(d.ThumbnailKey),
            canPreview = me.IsSuperAdmin,
            canEdit = me.Can(AppModules.DashboardConfig, PermissionLevel.Edit),
            canEditGroups = me.Can(AppModules.Groups, PermissionLevel.Edit),
            canModify = me.Can(AppModules.Publishing, PermissionLevel.Edit),
            isSuperAdmin = me.IsSuperAdmin,
            versionsKept = Rules.VersionsKept,
            noRlsMessage = GroupService.NoRlsMessage,
        });
    }

    public sealed record DetailsBody(
        string Name, string? Description, int? CategoryId, int PrimaryOwnerId, int? BackupOwnerId, List<string>? Tags,
        Audience Audience, DataClassification DataClassification, string? SharePointFolder, string? SharePointSubFolder);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, DetailsBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await master.UpdateAsync(id, new DashboardDetailsInput(body.Name, body.Description, body.CategoryId, body.PrimaryOwnerId, body.BackupOwnerId,
                body.Tags, body.Audience, body.DataClassification, body.SharePointFolder, body.SharePointSubFolder), Current.UserId!.Value, ct);
            return NoContent();
        });
    }

    [HttpPost("{id:int}/thumbnail")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> SetThumbnail(int id, IFormFile? file, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a PNG or JPG image." });
        if (file.Length > Rules.ThumbnailMaxBytes) return BadRequest(new { message = "The thumbnail is larger than 1 MB." });
        var data = new byte[file.Length];
        await using (var s = file.OpenReadStream()) await s.ReadExactlyAsync(data, ct);
        return await Guard(async () =>
        {
            var key = await master.SetThumbnailAsync(id, data, ct);
            return Ok(new { thumbnail = ThumbnailVersion(key) });
        });
    }

    [HttpDelete("{id:int}/thumbnail")]
    public async Task<IActionResult> RemoveThumbnail(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () => { await master.RemoveThumbnailAsync(id, ct); return NoContent(); });
    }

    /// <summary>Re-reads the RLS flag from the Power BI semantic model.</summary>
    [HttpPost("{id:int}/check-rls")]
    public async Task<IActionResult> CheckRls(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var (rls, changed) = await master.SyncRlsAsync(id, ct);
            return Ok(new { rlsEnabled = rls, changed });
        });
    }

    /// <summary>Modify Dashboard: upload a new .pbix that replaces the report in its Power BI workspace. Everything else stays.</summary>
    [HttpPost("{id:int}/modify")]
    [RequestSizeLimit(1L * 1024 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> Modify(int id, IFormFile? file, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Publishing, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a .pbix file." });
        return await Guard(async () =>
        {
            await using var stream = file.OpenReadStream();
            return Ok(await versions.ReplaceAsync(id, stream, file.FileName, Current.UserId!.Value, ct));
        });
    }

    /// <summary>Progress of Modify Dashboard or a restore; polled by the page until Succeeded or Failed.</summary>
    [HttpGet("{id:int}/modify-status")]
    public async Task<IActionResult> ModifyStatus(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.View, ct) is { } denied) return denied;
        return await Guard(async () => Ok(await versions.GetStatusAsync(id, ct)));
    }

    /// <summary>Downloads a kept file version. Super Admins only.</summary>
    [HttpGet("{id:int}/versions/{number:int}/download")]
    public async Task<IActionResult> DownloadVersion(int id, int number, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (!me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can download file versions." });
        try
        {
            var (content, name) = await versions.OpenVersionAsync(id, number, ct);
            return File(content, "application/octet-stream", name);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            return NotFound(new { message = "That version's file isn't available." });
        }
    }

    /// <summary>Restores a kept version: re-imports its file over the live report as a new version. Super Admins only.</summary>
    [HttpPost("{id:int}/versions/{number:int}/restore")]
    public async Task<IActionResult> RestoreVersion(int id, int number, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (!me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can restore file versions." });
        return await Guard(async () => Ok(await versions.RestoreAsync(id, number, me.Id, ct)));
    }

    public sealed record GroupBody(string Name, string? RlsValue);

    /// <summary>Adds a group. Only dashboards with RLS can have more than the default group.</summary>
    [HttpPost("{id:int}/groups")]
    public async Task<IActionResult> AddGroup(int id, GroupBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var g = await groups.AddAsync(id, body.Name, body.RlsValue, Current.UserId!.Value, ct);
            return Ok(new { g.Id, g.Name });
        });
    }

    public sealed record GroupRlsBody(string? RlsValue);

    /// <summary>Changes a group's RLS value (one or more report role names, comma-separated).</summary>
    [HttpPut("{id:int}/groups/{groupId:int}")]
    public async Task<IActionResult> UpdateGroup(int id, int groupId, GroupRlsBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Groups, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var g = await groups.SetRlsAsync(id, groupId, body.RlsValue, ct);
            return Ok(new { g.Name, g.RlsValue });
        });
    }

    /// <summary>
    /// Embed details for a Super Admin's preview as the members of one group would see it. Needs no membership, is not
    /// counted as a view, and is audited.
    /// </summary>
    [HttpGet("{id:int}/preview-embed")]
    public async Task<IActionResult> PreviewEmbed(int id, int groupId, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.DashboardConfig, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        if (!me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can preview dashboards from here. Everyone else opens dashboards in the User Portal." });
        return await Guard(async () => Ok(await embed.PreviewAsync(id, groupId, me.Id, ct)));
    }

    private static object Limits => new
    {
        nameMax = Rules.DashboardNameMax, descriptionMax = Rules.DescriptionMax, tagsMax = Rules.TagsPerDashboardMax, tagMax = Rules.TagMax,
        thumbnailMaxBytes = Rules.ThumbnailMaxBytes, sharePointMax = Rules.SharePointPathMax,
    };

    /// <summary>A short token that changes whenever the thumbnail changes, so the browser can cache by it; null when there is none.</summary>
    internal static string? ThumbnailVersion(string? key) => key is null ? null : Path.GetFileNameWithoutExtension(key);
}

/// <summary>Progress of the setup steps shown on the Admin Portal overview.</summary>
[Route("api/admin/setup")]
public sealed class SetupController(CurrentUser current, AppDbContext db) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (await Current.GetAsync(ct) is null) return Unauthorized();
        var customRoles = await db.Roles.CountAsync(r => !r.IsSystem, ct);
        var roles = await db.Roles.CountAsync(ct);
        var users = await db.Users.CountAsync(ct);
        var tenants = await db.BiTenants.CountAsync(t => t.IsActive, ct);
        var verified = await db.BiTenants.CountAsync(t => t.IsActive && t.LastVerifyPassed == true, ct);
        var categories = await db.Categories.CountAsync(ct);
        var published = await db.Dashboards.CountAsync(d => d.Status == DashboardStatus.Active, ct);
        var uncategorised = await db.Dashboards.CountAsync(d => d.Status != DashboardStatus.Retired && d.CategoryId == null, ct);
        return Ok(new { roles, customRoles, users, tenants, verifiedTenants = verified, categories, activeDashboards = published, uncategorised });
    }
}
