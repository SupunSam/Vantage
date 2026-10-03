using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>A business rule was broken; the message is shown to the admin as is.</summary>
public sealed class RuleException(string message) : Exception(message);

public sealed record RoleInput(string Name, string? Description, IReadOnlyDictionary<string, PermissionLevel> Permissions);

/// <summary>
/// Role Master rules: names unique (1–60 characters); built-in roles can't be renamed or deleted;
/// Super Admin always has Edit on every module; a role with users can't be deleted.
/// </summary>
public sealed class RoleService(AppDbContext db, AuditWriter audit, TimeProvider clock)
{
    private static readonly HashSet<string> ModuleKeys = AppModules.All.Select(m => m.Key).ToHashSet();

    public async Task<Role> CreateAsync(RoleInput input, CancellationToken ct)
    {
        var name = ValidName(input.Name);
        if (await db.Roles.AnyAsync(r => r.Name == name, ct)) throw new RuleException($"A role named '{name}' already exists.");

        var role = new Role { Name = name, Description = input.Description?.Trim(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        role.Permissions = Normalise(input.Permissions).Select(p => new RolePermission { Module = p.Key, Level = p.Value }).ToList();
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        audit.Add("role.created", "Role", role.Id, details: new { role.Name, permissions = input.Permissions });
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<Role> UpdateAsync(int id, RoleInput input, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        var name = ValidName(input.Name);

        if (role.IsSystem && name != role.Name) throw new RuleException("Built-in roles can't be renamed.");
        if (name != role.Name && await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct))
            throw new RuleException($"A role named '{name}' already exists.");

        role.Name = name;
        role.Description = input.Description?.Trim();

        if (role.Name == SystemRoles.SuperAdmin)
        {
            // Super Admin keeps full access whatever is sent.
        }
        else
        {
            var wanted = Normalise(input.Permissions);
            db.RolePermissions.RemoveRange(role.Permissions);
            role.Permissions = wanted.Select(p => new RolePermission { RoleId = role.Id, Module = p.Key, Level = p.Value }).ToList();
        }

        audit.Add("role.updated", "Role", role.Id, details: new { role.Name, permissions = input.Permissions });
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        if (role.IsSystem) throw new RuleException("Built-in roles can't be deleted.");
        var users = await db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
        if (users > 0) throw new RuleException($"'{role.Name}' is assigned to {users} user{(users == 1 ? "" : "s")}. Remove it from them first.");

        db.Roles.Remove(role);
        audit.Add("role.deleted", "Role", id, details: new { role.Name });
        await db.SaveChangesAsync(ct);
    }

    private static string ValidName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 60) throw new RuleException("Role name is required, up to 60 characters.");
        return n;
    }

    /// <summary>Keeps known modules with View or Edit; None is simply not stored.</summary>
    private static Dictionary<string, PermissionLevel> Normalise(IReadOnlyDictionary<string, PermissionLevel> perms)
    {
        var unknown = perms.Keys.Where(k => !ModuleKeys.Contains(k)).ToList();
        if (unknown.Count > 0) throw new RuleException("Unknown module: " + string.Join(", ", unknown));
        return perms.Where(p => p.Value != PermissionLevel.None).ToDictionary(p => p.Key, p => p.Value);
    }
}
