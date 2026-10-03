using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>Category Master: the Primary / Secondary / Tertiary tree that dashboards sit in.</summary>
[Route("api/admin/categories")]
public sealed class CategoriesController(CurrentUser current, CategoryService categories) : AdminControllerBase(current)
{
    /// <summary>The whole tree in display order. Also read by the publish form and Dashboards Master.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        if (!me.Can(AppModules.Categories, PermissionLevel.View) && !me.Can(AppModules.Publishing, PermissionLevel.View)
            && !me.Can(AppModules.DashboardConfig, PermissionLevel.View))
            return StatusCode(403, new { message = "You need View permission on Categories." });
        return Ok(await categories.ListAsync(ct));
    }

    public sealed record CategoryBody(string? Name, int? ParentId);

    [HttpPost]
    public async Task<IActionResult> Create(CategoryBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Categories, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var c = await categories.CreateAsync(body.Name, body.ParentId, ct);
            return Ok(new { c.Id, c.Name, c.Level });
        });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Rename(int id, CategoryBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Categories, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var c = await categories.RenameAsync(id, body.Name, ct);
            return Ok(new { c.Id, c.Name });
        });
    }

    public sealed record MoveBody(int Direction);

    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, MoveBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Categories, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await categories.MoveAsync(id, body.Direction, ct);
            return NoContent();
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Categories, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await categories.DeleteAsync(id, ct);
            return NoContent();
        });
    }
}
