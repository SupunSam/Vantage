using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>Admin Portal endpoints: each action checks the signed-in user's permission on one module.</summary>
[ApiController, Authorize]
public abstract class AdminControllerBase(CurrentUser current) : ControllerBase
{
    protected CurrentUser Current { get; } = current;

    /// <summary>Returns an error result when the user lacks the permission, otherwise null.</summary>
    protected async Task<IActionResult?> RequireAsync(string module, PermissionLevel level, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (me.Can(module, level)) return null;
        var name = AppModules.All.FirstOrDefault(m => m.Key == module).Name ?? module;
        return StatusCode(403, new { message = $"You need {level} permission on {name}." });
    }

    /// <summary>
    /// Like <see cref="RequireAsync"/>, but for one dashboard: the permission on the module, or being an owner of that dashboard (C59).
    /// Owners manage their own dashboards (details, groups, members) from the User Portal through these same endpoints.
    /// </summary>
    protected async Task<IActionResult?> RequireOnDashboardAsync(AppDbContext db, string module, PermissionLevel level, int dashboardId, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (me.Can(module, level)) return null;
        if (await Vantage.Infrastructure.Services.DashboardOwners.IsAsync(db, me.Id, dashboardId, ct)) return null;
        var name = AppModules.All.FirstOrDefault(m => m.Key == module).Name ?? module;
        return StatusCode(403, new { message = $"You need {level} permission on {name}, or to own this dashboard." });
    }

    /// <summary>The permission on the module, or owning at least one dashboard (for the pickers an owner's screens need).</summary>
    protected async Task<IActionResult?> RequireOrOwnerAsync(AppDbContext db, string module, PermissionLevel level, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (me.Can(module, level)) return null;
        if (await Vantage.Infrastructure.Services.DashboardOwners.OwnsAnyAsync(db, me.Id, ct)) return null;
        var name = AppModules.All.FirstOrDefault(m => m.Key == module).Name ?? module;
        return StatusCode(403, new { message = $"You need {level} permission on {name}." });
    }

    /// <summary>Returns an error result unless the signed-in user is a Super Admin. For the few things only Super Admins do (publishing, creating users).</summary>
    protected async Task<IActionResult?> RequireSuperAdminAsync(string what, CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        return me.IsSuperAdmin ? null : StatusCode(403, new { message = $"Only Super Admins can {what}." });
    }

    /// <summary>Runs an action and turns rule breaks into 400, missing records into 404 and BI platform errors into 502.</summary>
    protected async Task<IActionResult> Guard(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Not found." }); }
        catch (EmbedException ex) { return StatusCode(502, new { code = ex.Code, message = ex.Message }); }
    }
}
