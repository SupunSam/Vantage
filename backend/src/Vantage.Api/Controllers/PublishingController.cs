using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.GenAi;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>Publishing (Admin Portal). Only Super Admins publish (C40): Edit actions are refused for everyone else, whatever their role permissions say.</summary>
[ApiController, Authorize, Route("api/publishing")]
public sealed class PublishingController(
    AppDbContext db, CurrentUser current, PublishingService publishing, GenAiPublisher genAiPublisher, GenAiService genAi, TableauPublisher tableauPublisher,
    CategoryService categoryService, DashboardMasterService master) : ControllerBase
{
    /// <summary>The code a dashboard of this name gets (C60): the existing one when the name is already a dashboard, else a new unique one.</summary>
    private async Task<string> CodeForAsync(string? name, CancellationToken ct)
    {
        var n = (name ?? "").Trim();
        var existing = await db.Dashboards.AsNoTracking().Where(d => d.Name == n).Select(d => d.Code).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var taken = (await db.Dashboards.AsNoTracking().Select(d => d.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DashboardCodes.Generate(n, taken.Contains);
    }

    [HttpGet("code")]
    public async Task<IActionResult> Code([FromQuery] string? name, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        return Ok(new { code = await CodeForAsync(name, ct) });
    }

    public sealed record DraftBody(int? Id, BiType Type, string? Name, string Payload);

    /// <summary>The signed-in person's saved drafts, newest first.</summary>
    [HttpGet("drafts")]
    public async Task<IActionResult> Drafts(CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        return Ok(await db.PublishDrafts.AsNoTracking().Where(d => d.UserId == me.Id).OrderByDescending(d => d.UpdatedAtUtc)
            .Select(d => new { d.Id, type = d.Type.ToString(), d.Title, d.Payload, d.UpdatedAtUtc }).ToListAsync(ct));
    }

    [HttpPost("drafts")]
    public async Task<IActionResult> SaveDraft(DraftBody body, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        if (body.Payload.Length > 40_000) return BadRequest(new { message = "That draft is too big to save." });
        try { System.Text.Json.JsonDocument.Parse(body.Payload); } catch (System.Text.Json.JsonException) { return BadRequest(new { message = "That draft isn't valid." }); }
        var title = string.IsNullOrWhiteSpace(body.Name) ? "Untitled draft" : body.Name.Trim();
        if (title.Length > 200) title = title[..200];
        var draft = body.Id is { } id ? await db.PublishDrafts.SingleOrDefaultAsync(d => d.Id == id && d.UserId == me.Id, ct) : null;
        if (draft is null) { draft = new PublishDraft { UserId = me.Id }; db.PublishDrafts.Add(draft); }
        draft.Type = body.Type;
        draft.Title = title;
        draft.Payload = body.Payload;
        draft.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return Ok(new { draft.Id, type = draft.Type.ToString(), draft.Title, draft.Payload, draft.UpdatedAtUtc });
    }

    [HttpDelete("drafts/{id:int}")]
    public async Task<IActionResult> DeleteDraft(int id, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        await db.PublishDrafts.Where(d => d.Id == id && d.UserId == me.Id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    /// <summary>Choices for the publish form: Power BI tenants with their workspaces, categories, users for owner pickers.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;

        var tenants = await db.BiTenants.AsNoTracking()
            .Where(t => t.Platform == BiPlatform.PowerBi && t.IsActive)
            .Select(t => new
            {
                t.Id, t.Name, verified = t.LastVerifyPassed == true, t.LastVerifiedAtUtc,
                workspaces = t.Workspaces.Where(w => w.IsActive).Select(w => new { w.Id, w.Name, w.WorkspaceId, w.ServicePrincipalAccess, w.OnDedicatedCapacity }).ToList(),
            })
            .ToListAsync(ct);

        // Tableau Server tenants for the Tableau tab (a Tableau Public view needs none). Verify isn't required to publish, so it is shown, not enforced.
        var tableauTenants = await db.BiTenants.AsNoTracking()
            .Where(t => t.Platform == BiPlatform.Tableau && t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.ServerUrl, t.SiteContentUrl, verified = t.LastVerifyPassed == true })
            .ToListAsync(ct);

        var categories = await categoryService.ListAsync(ct);

        var users = await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(ct);

        return Ok(new
        {
            tenants,
            tableauTenants,
            categories,
            users,
            limits = new
            {
                nameMax = Rules.DashboardNameMax, codeMax = Rules.DashboardCodeMax, descriptionMax = Rules.DescriptionMax, pbixMaxMb = 1024,
                tagsMax = Rules.TagsPerDashboardMax, tagMax = Rules.TagMax, thumbnailMaxBytes = Rules.ThumbnailMaxBytes,
            },
            genAi = new
            {
                maxBytes = Rules.GenAiMaxBytes, warnBytes = (await genAi.CurrentSettingsAsync(ct)).WarnBytes, versionsKept = Rules.VersionsKept,
                approvedHosts = await genAi.ApprovedHostsAsync(ct),
            },
        });
    }

    public sealed class PublishPowerBiForm
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string? Description { get; set; }
        public int WorkspaceId { get; set; }
        public int? PrimaryOwnerId { get; set; }
        public int? BackupOwnerId { get; set; }
        public bool RlsEnabled { get; set; }
        public string? DefaultGroupRlsValue { get; set; }
        public int? CategoryId { get; set; }
        /// <summary>Comma-separated, up to 8.</summary>
        public string? Tags { get; set; }
        public Audience Audience { get; set; } = Audience.Internal;
        public DataClassification DataClassification { get; set; } = DataClassification.Internal;
        public IFormFile? File { get; set; }
        public IFormFile? Thumbnail { get; set; }
    }

    /// <summary>
    /// Uploads a .pbix and starts the import into the chosen workspace. Returns at once with status Publishing;
    /// the Admin Portal then polls GET {id}/status until the dashboard is Active or Failed.
    /// </summary>
    [HttpPost("powerbi")]
    [RequestSizeLimit(1L * 1024 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> PublishPowerBi([FromForm] PublishPowerBiForm form, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        if (form.File is null || form.File.Length == 0) return BadRequest(new { message = "Choose a .pbix file." });

        try
        {
            // Check the thumbnail before anything is created, so a bad image never leaves a half-published dashboard.
            byte[]? thumbnail = null;
            if (form.Thumbnail is { Length: > 0 })
            {
                if (form.Thumbnail.Length > Rules.ThumbnailMaxBytes) return BadRequest(new { message = "The thumbnail is larger than 1 MB." });
                thumbnail = new byte[form.Thumbnail.Length];
                await using (var t = form.Thumbnail.OpenReadStream()) await t.ReadExactlyAsync(thumbnail, ct);
                Thumbnails.Validate(thumbnail);
            }

            await using var stream = form.File.OpenReadStream();
            var status = await publishing.PublishPowerBiAsync(new PublishPowerBiRequest(
                form.Name, await CodeForAsync(form.Name, ct), form.Description, form.WorkspaceId, form.PrimaryOwnerId ?? me.Id, form.BackupOwnerId,
                form.RlsEnabled, form.DefaultGroupRlsValue, form.CategoryId, form.File.FileName,
                Rules.NormalizeTags([form.Tags ?? ""]), form.Audience, form.DataClassification), stream, me.Id, ct);
            if (thumbnail is not null) await master.SetThumbnailAsync(status.DashboardId, thumbnail, ct);
            return Ok(status);
        }
        catch (Exception ex) when (ex is ArgumentException or RuleException)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (EmbedException ex)
        {
            return StatusCode(502, new { code = ex.Code, message = ex.Message });
        }
    }

    public sealed class PublishTableauForm
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string? Description { get; set; }
        /// <summary>The Tableau Server tenant; empty for a Tableau Public view.</summary>
        public int? TenantId { get; set; }
        public string ViewUrl { get; set; } = "";
        public int? PrimaryOwnerId { get; set; }
        public int? BackupOwnerId { get; set; }
        public int? CategoryId { get; set; }
        /// <summary>Comma-separated, up to 8.</summary>
        public string? Tags { get; set; }
        public Audience Audience { get; set; } = Audience.Internal;
        public DataClassification DataClassification { get; set; } = DataClassification.Internal;
        public IFormFile? Thumbnail { get; set; }
    }

    /// <summary>Publishes a Tableau dashboard (a Tableau Server view, or a Tableau Public view). It is live at once, with the owners in the default group.</summary>
    [HttpPost("tableau")]
    [RequestSizeLimit(3L * 1024 * 1024)]
    public async Task<IActionResult> PublishTableau([FromForm] PublishTableauForm form, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        try
        {
            var thumbnail = await ReadThumbnailAsync(form.Thumbnail, ct);
            var status = await tableauPublisher.PublishAsync(new PublishTableauRequest(
                form.Name, await CodeForAsync(form.Name, ct), form.Description, form.TenantId, form.ViewUrl, form.PrimaryOwnerId ?? me.Id, form.BackupOwnerId, form.CategoryId,
                Rules.NormalizeTags([form.Tags ?? ""]), form.Audience, form.DataClassification), me.Id, ct);
            if (thumbnail is not null) await master.SetThumbnailAsync(status.DashboardId, thumbnail, ct);
            return Ok(status);
        }
        catch (Exception ex) when (ex is ArgumentException or RuleException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    public sealed record CheckTableauBody(int? TenantId, string? ViewUrl);

    /// <summary>Reads a view address the way publishing will, without saving anything, so the form can say what is wrong before the real publish.</summary>
    [HttpPost("tableau/check")]
    public async Task<IActionResult> CheckTableau(CheckTableauBody body, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        try
        {
            var tenant = await tableauPublisher.ResolveTenantAsync(body.TenantId, ct);
            var view = TableauViewUrl.Parse(body.ViewUrl, tenant?.ServerUrl, tenant?.SiteContentUrl);
            return Ok(new { view.IsPublic, view.Src, view.Host, view.Workbook, view.View, view.Site, warning = TableauPublisher.Warning(tenant, view) });
        }
        catch (RuleException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    public sealed class PublishGenAiForm
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string? Description { get; set; }
        public int? PrimaryOwnerId { get; set; }
        public int? BackupOwnerId { get; set; }
        public int? CategoryId { get; set; }
        /// <summary>Comma-separated, up to 8.</summary>
        public string? Tags { get; set; }
        public Audience Audience { get; set; } = Audience.Internal;
        public DataClassification DataClassification { get; set; } = DataClassification.Internal;
        public IFormFile? File { get; set; }
        public IFormFile? Thumbnail { get; set; }
    }

    /// <summary>
    /// Uploads a GenAI dashboard (one .html file made from the approved template). The file is checked first; if it
    /// passes, the dashboard is created and is live straight away (there is no import step).
    /// </summary>
    [HttpPost("genai")]
    [RequestSizeLimit(20L * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 20L * 1024 * 1024)]
    public async Task<IActionResult> PublishGenAi([FromForm] PublishGenAiForm form, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await current.GetAsync(ct))!;
        // GenAI pages are code, so only Super Admins vet and publish them.
        if (!me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can publish GenAI dashboards." });
        if (form.File is null || form.File.Length == 0) return BadRequest(new { message = "Choose a .html file." });

        try
        {
            var thumbnail = await ReadThumbnailAsync(form.Thumbnail, ct);
            await using var stream = form.File.OpenReadStream();
            var status = await genAiPublisher.PublishAsync(new PublishGenAiRequest(
                form.Name, await CodeForAsync(form.Name, ct), form.Description, form.PrimaryOwnerId ?? me.Id, form.BackupOwnerId, form.CategoryId, form.File.FileName,
                Rules.NormalizeTags([form.Tags ?? ""]), form.Audience, form.DataClassification), stream, me.Id, ct);
            if (thumbnail is not null) await master.SetThumbnailAsync(status.DashboardId, thumbnail, ct);
            return Ok(status);
        }
        catch (Exception ex) when (ex is ArgumentException or RuleException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Checks a file against the template rules without publishing it, so authors can fix problems before the real upload.</summary>
    [HttpPost("genai/check")]
    [RequestSizeLimit(20L * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 20L * 1024 * 1024)]
    public async Task<IActionResult> CheckGenAi(IFormFile? file, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a .html file." });
        var ext = Path.GetExtension(file.FileName);
        if (!ext.Equals(".html", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Choose a single .html file." });
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var result = await genAi.CheckAsync(ms.ToArray(), ct);
        return Ok(new { passed = result.Passed, result.Errors, result.Warnings, result.Libraries, result.SizeBytes });
    }

    /// <summary>The approved starter template, to give to whoever (or whatever AI tool) builds the dashboard.</summary>
    [HttpGet("genai/template")]
    public async Task<IActionResult> GenAiStarterTemplate(CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        return File(GenAiTemplate.Read(), "text/html", GenAiTemplate.FileName);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is not { Length: > 0 }) return null;
        if (file.Length > Rules.ThumbnailMaxBytes) throw new RuleException("The thumbnail is larger than 1 MB.");
        var bytes = new byte[file.Length];
        await using (var t = file.OpenReadStream()) await t.ReadExactlyAsync(bytes, ct);
        Thumbnails.Validate(bytes);
        return bytes;
    }

    [HttpGet("{id:int}/status")]
    public async Task<IActionResult> Status(int id, CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        try { return Ok(await publishing.GetStatusAsync(id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    /// <summary>Recently published Power BI dashboards, newest first, for the publish page.</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> Recent(CancellationToken ct)
    {
        if (await RequireAsync(PermissionLevel.View, ct) is { } denied) return denied;
        var rows = await db.Dashboards.AsNoTracking()
            .Where(d => d.Type == BiType.PowerBi && d.Versions.Any())
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(20)
            .Select(d => new
            {
                d.Id, d.Name, d.Code, status = d.Status.ToString(), d.RlsEnabled, d.LastError, d.PowerBiReportId,
                workspace = d.Workspace != null ? d.Workspace.Name : null,
                owner = d.PrimaryOwner != null ? d.PrimaryOwner.DisplayName : null,
                groups = d.Groups.OrderByDescending(g => g.IsDefault).ThenBy(g => g.Name).Select(g => new
                {
                    g.Id, g.Name, g.RlsValue, g.IsDefault,
                    members = g.Members.Where(m => m.RemovedAtUtc == null).Select(m => m.User.Email).ToList(),
                }).ToList(),
                d.CreatedAtUtc,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    private async Task<IActionResult?> RequireAsync(PermissionLevel level, CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        // Super Admins are the publishers (C40): anyone else may not publish, whatever their role permissions say.
        if (level == PermissionLevel.Edit && !me.IsSuperAdmin) return StatusCode(403, new { message = "Only Super Admins can publish dashboards." });
        return me.Can(AppModules.Publishing, level) ? null : StatusCode(403, new { message = "You need Publishing permission." });
    }
}
