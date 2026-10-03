using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
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
