using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>
/// DEVELOPMENT ONLY: the sign-in picker that stands in for ADFS and Cognito in the local build.
/// Every action returns 404 outside Development.
/// </summary>
[ApiController, Route("api/dev")]
public sealed class DevController(IWebHostEnvironment env, AppDbContext db) : ControllerBase
{
    /// <summary>Users for the dev sign-in picker, with their roles.</summary>
    [HttpGet("users"), AllowAnonymous]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Status != UserStatus.Inactive)
            .OrderBy(u => u.UserType).ThenBy(u => u.DisplayName)
            .Select(u => new
            {
                u.Email, u.DisplayName, userType = u.UserType.ToString(), status = u.Status.ToString(),
                title = u.HrmsProfile != null ? u.HrmsProfile.JobTitle : null,
                roles = u.Roles.Select(r => r.Role.Name).ToList(),
            })
            .ToListAsync(ct);
        return Ok(users);
    }

}
