using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Api.Controllers;

/// <summary>
/// Admin Configuration (module "admin-config"): branding, settings, approved CDNs and dashboard types.
/// Anyone with View can read; Edit changes things. Every change is audited.
/// </summary>
[Route("api/admin/config")]
public sealed class AdminConfigController(CurrentUser current, ConfigService config) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.View, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        return Ok(new
        {
            canEdit = me.Can(AppModules.AdminConfig, PermissionLevel.Edit),
            settings = await config.ListAsync(ct),
            cdns = await config.CdnsAsync(ct),
            types = await config.TypesAsync(ct),
            logoMaxBytes = ConfigService.LogoMaxBytes,
            canEditGenAi = me.IsSuperAdmin && me.Can(AppModules.AdminConfig, PermissionLevel.Edit),
        });
    }

    /// <summary>GenAI settings and the approved CDN list decide what code may run in front of staff, so only Super Admins change them.</summary>
    private IActionResult GenAiSuperAdminOnly() => StatusCode(403, new { message = "Only Super Admins can change the GenAI settings and the approved CDNs." });

    public sealed record SettingsBody(Dictionary<string, string?> Values);

    [HttpPut("settings")]
    public async Task<IActionResult> Save(SettingsBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        if ((body.Values ?? []).Keys.Any(k => k.StartsWith(SettingKeys.GenAiPrefix, StringComparison.Ordinal)) && !me.IsSuperAdmin) return GenAiSuperAdminOnly();
        return await Guard(async () => Ok(new { changed = await config.UpdateAsync(body.Values ?? [], me.Id, ct) }));
    }

    [HttpPost("logo"), RequestSizeLimit(ConfigService.LogoMaxBytes + 64 * 1024)]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        return await Guard(async () =>
        {
            await using var stream = file.OpenReadStream();
            await config.SaveLogoAsync(stream, file.FileName, me.Id, ct);
            return NoContent();
        });
    }

    [HttpDelete("logo")]
    public async Task<IActionResult> ResetLogo(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = (await Current.GetAsync(ct))!;
        await config.ResetLogoAsync(me.Id, ct);
        return NoContent();
    }

    public sealed record CdnBody(string? Host, string? Notes, bool IsActive = true);

    [HttpPost("cdns")]
    public async Task<IActionResult> AddCdn(CdnBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (!(await Current.GetAsync(ct))!.IsSuperAdmin) return GenAiSuperAdminOnly();
        return await Guard(async () => Ok(await config.AddCdnAsync(body.Host, body.Notes, ct)));
    }

    [HttpPut("cdns/{id:int}")]
    public async Task<IActionResult> UpdateCdn(int id, CdnBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (!(await Current.GetAsync(ct))!.IsSuperAdmin) return GenAiSuperAdminOnly();
        return await Guard(async () => { await config.UpdateCdnAsync(id, body.Notes, body.IsActive, ct); return NoContent(); });
    }

    [HttpDelete("cdns/{id:int}")]
    public async Task<IActionResult> RemoveCdn(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (!(await Current.GetAsync(ct))!.IsSuperAdmin) return GenAiSuperAdminOnly();
        return await Guard(async () => { await config.RemoveCdnAsync(id, ct); return NoContent(); });
    }

    public sealed record TypeBody(bool IsEnabled, int? MaxFileSizeMb);

    [HttpPut("types/{type}")]
    public async Task<IActionResult> UpdateType(string type, TypeBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.AdminConfig, PermissionLevel.Edit, ct) is { } denied) return denied;
        if (!Enum.TryParse<DashboardType>(type, true, out var t)) return NotFound();
        return await Guard(async () => { await config.UpdateTypeAsync(t, body.IsEnabled, body.MaxFileSizeMb, ct); return NoContent(); });
    }
}
