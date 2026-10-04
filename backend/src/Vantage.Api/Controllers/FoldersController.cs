using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>User Portal personal folders. Every call works on the signed-in user's own folders only.</summary>
[ApiController, Authorize, Route("api/folders")]
public sealed class FoldersController(CurrentUser current, PersonalFolderService folders) : ControllerBase
{
    public sealed record NameBody(string? Name);

    [HttpGet]
    public Task<IActionResult> List(CancellationToken ct) => Run(async me => Ok(await folders.ListAsync(me, ct)));

    [HttpPost]
    public Task<IActionResult> Create(NameBody body, CancellationToken ct) => Run(async me => Ok(await folders.CreateAsync(me, body.Name, ct)));

    [HttpPut("{id:int}")]
    public Task<IActionResult> Rename(int id, NameBody body, CancellationToken ct) => Run(async me => { await folders.RenameAsync(me, id, body.Name, ct); return NoContent(); });

    [HttpDelete("{id:int}")]
    public Task<IActionResult> Delete(int id, CancellationToken ct) => Run(async me => { await folders.DeleteAsync(me, id, ct); return NoContent(); });

    [HttpPost("{id:int}/dashboards/{dashboardId:int}")]
    public Task<IActionResult> Add(int id, int dashboardId, CancellationToken ct) => Run(async me => { await folders.AddDashboardAsync(me, id, dashboardId, ct); return NoContent(); });

    [HttpDelete("{id:int}/dashboards/{dashboardId:int}")]
    public Task<IActionResult> Remove(int id, int dashboardId, CancellationToken ct) => Run(async me => { await folders.RemoveDashboardAsync(me, id, dashboardId, ct); return NoContent(); });

    private async Task<IActionResult> Run(Func<int, Task<IActionResult>> action)
    {
        var me = await current.GetAsync(HttpContext.RequestAborted);
        if (me is null) return Unauthorized();
        try { return await action(me.Id); }
        catch (RuleException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(new { message = "Not found." }); }
    }
}
