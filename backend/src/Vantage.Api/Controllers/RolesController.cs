using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

[Route("api/admin/roles")]
public sealed class RolesController(CurrentUser current, AppDbContext db, RoleService roles) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Roles, PermissionLevel.View, ct) is { } denied) return denied;
        var rows = await db.Roles.AsNoTracking()
            .OrderBy(r => r.Name == SystemRoles.SuperAdmin ? 0 : r.Name == SystemRoles.DashboardOwner ? 1 : r.Name == SystemRoles.DashboardUser ? 2 : 3).ThenBy(r => r.Name)
            .Select(r => new
            {
                r.Id, r.Name, r.Description, r.IsSystem,
                locked = r.Name == SystemRoles.SuperAdmin,
                users = r.Users.Count(u => u.User.Status != UserStatus.Inactive),
                permissions = r.Permissions.Select(p => new { p.Module, level = p.Level.ToString() }).ToList(),
            })
            .ToListAsync(ct);
        return Ok(new
        {
            roles = rows,
            modules = AppModules.All.Select(m => new { key = m.Key, name = m.Name, portal = m.Portal }),
        });
    }

    public sealed record RoleBody(string Name, string? Description, Dictionary<string, PermissionLevel> Permissions);

    [HttpPost]
    public async Task<IActionResult> Create(RoleBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Roles, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var role = await roles.CreateAsync(new RoleInput(body.Name, body.Description, body.Permissions), ct);
            return Ok(new { role.Id, role.Name });
        });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, RoleBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Roles, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var role = await roles.UpdateAsync(id, new RoleInput(body.Name, body.Description, body.Permissions), ct);
            return Ok(new { role.Id, role.Name });
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Roles, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await roles.DeleteAsync(id, ct);
            return NoContent();
        });
    }
}
