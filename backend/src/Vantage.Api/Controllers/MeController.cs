using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;
using Vantage.Infrastructure.Storage;

namespace Vantage.Api.Controllers;

[ApiController, Authorize, Route("api/me")]
public sealed class MeController(CurrentUser current, ConfigService config) : ControllerBase
{
    /// <summary>Settings that change how the portals behave for the signed-in person: idle timeout and default rows per page.</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        if (await current.GetAsync(ct) is null) return Unauthorized();
        return Ok(await config.UiAsync(ct));
    }

    /// <summary>The signed-in user, roles and module permissions; the portals build their menus from this.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await current.GetAsync(ct);
        if (me is null) return Unauthorized();
        return Ok(new
        {
            me.Id, me.Email, me.DisplayName, userType = me.UserType.ToString(), me.Roles, me.IsSuperAdmin,
            permissions = me.Permissions.ToDictionary(p => p.Key, p => p.Value.ToString()),
        });
    }
}

[ApiController, Route("api/branding")]
public sealed class BrandingController(AppDbContext db, IFileStore files) : ControllerBase
{
    /// <summary>Public: the login page needs the portal name, logo and colours before sign-in.</summary>
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("branding.") && s.Key != SettingKeys.BrandLogoFile)
            .ToDictionaryAsync(s => s.Key["branding.".Length..], s => s.Value, ct);
        return Ok(values);
    }

    /// <summary>The uploaded logo. Public, like the branding itself, because the sign-in page shows it. Cached by the ?v= token in the URL.</summary>
    [HttpGet("logo"), AllowAnonymous]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        var key = await db.SystemSettings.AsNoTracking().Where(s => s.Key == SettingKeys.BrandLogoFile).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key)) return NotFound();
        var type = Path.GetExtension(key).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => null };
        if (type is null) return NotFound();
        try
        {
            Response.Headers.CacheControl = "public, max-age=86400";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return File(files.OpenRead(key), type);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return NotFound(); }
    }
}
