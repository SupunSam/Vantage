using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Auth;

/// <summary>The signed-in user, their roles and effective module permissions (union across roles).</summary>
public sealed record CurrentUserInfo(int Id, string Email, string DisplayName, UserType UserType, IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, PermissionLevel> Permissions)
{
    public bool IsSuperAdmin => Roles.Contains(SystemRoles.SuperAdmin);
    public bool Can(string module, PermissionLevel level) => Permissions.TryGetValue(module, out var l) && l >= level;
}

public sealed class CurrentUser(IHttpContextAccessor http, AppDbContext db) : IRequestContext
{
    private CurrentUserInfo? _info;
    private bool _loaded;

    public int? UserId => http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id ? int.Parse(id) : null;
    public string? CorrelationId => http.HttpContext?.TraceIdentifier;
    public string? IpAddress => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public async Task<CurrentUserInfo?> GetAsync(CancellationToken ct = default)
    {
        if (_loaded) return _info;
        _loaded = true;
        if (UserId is not { } id) return null;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.Status != UserStatus.Inactive)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.UserType,
                Roles = u.Roles.Select(r => r.Role.Name).ToList(),
                Perms = u.Roles.SelectMany(r => r.Role.Permissions).Select(p => new { p.Module, p.Level }).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        if (user is null) return null;

        var perms = user.Perms.GroupBy(p => p.Module).ToDictionary(g => g.Key, g => g.Max(p => p.Level));
        _info = new CurrentUserInfo(user.Id, user.Email, user.DisplayName ?? user.Email, user.UserType, user.Roles, perms);
        return _info;
    }
}
