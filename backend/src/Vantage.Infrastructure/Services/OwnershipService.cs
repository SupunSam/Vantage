using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Keeps the Dashboard Owner role in step with dashboard ownership:
/// given automatically when someone is named primary or backup owner, removed when they own nothing.
/// A Dashboard Owner role assigned by hand (IsAutomatic = false) is never removed here.
/// </summary>
public sealed class OwnershipService(AppDbContext db, TimeProvider clock)
{
    public async Task SyncOwnerRoleAsync(int userId, CancellationToken ct = default)
    {
        var ownerRoleId = await db.Roles.Where(r => r.Name == SystemRoles.DashboardOwner).Select(r => r.Id).SingleAsync(ct);

        // Dashboards loaded in this unit of work are judged by their pending values (so this works before SaveChanges);
        // all others by what is in the database.
        var tracked = db.ChangeTracker.Entries<Dashboard>().Where(e => e.State != EntityState.Deleted).Select(e => e.Entity).ToList();
        var trackedIds = tracked.Where(d => d.Id > 0).Select(d => d.Id).ToList();
        var ownsAny = tracked.Any(d => d.Status != DashboardStatus.Retired && (d.PrimaryOwnerId == userId || d.BackupOwnerId == userId))
            || await db.Dashboards.AnyAsync(d => !trackedIds.Contains(d.Id) && d.Status != DashboardStatus.Retired
                                                 && (d.PrimaryOwnerId == userId || d.BackupOwnerId == userId), ct);

        var existing = await db.UserRoles.FindAsync([userId, ownerRoleId], ct);
        if (ownsAny && existing is null)
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = ownerRoleId, IsAutomatic = true, AssignedAtUtc = clock.GetUtcNow().UtcDateTime });
        }
        else if (!ownsAny && existing is { IsAutomatic: true })
        {
            db.UserRoles.Remove(existing);
        }
    }
}
