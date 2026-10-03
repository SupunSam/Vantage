using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;

namespace Vantage.Api.Controllers;

[ApiController, Authorize, Route("api/me")]
public sealed class MeController(CurrentUser current) : ControllerBase
{
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
public sealed class BrandingController(AppDbContext db) : ControllerBase
{
    /// <summary>Public: the login page needs the portal name, logo and colours before sign-in.</summary>
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("branding."))
            .ToDictionaryAsync(s => s.Key["branding.".Length..], s => s.Value, ct);
        return Ok(values);
    }
}
