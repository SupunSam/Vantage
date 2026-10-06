using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Who counts as an owner for managing a dashboard (C59): the primary or backup owner of a dashboard that isn't retired and isn't
/// waiting for a Super Admin to confirm its owners (C43). Owners manage their own dashboards from My Dashboards in the User Portal.
/// </summary>
public static class DashboardOwners
{
    public static Task<bool> IsAsync(AppDbContext db, int userId, int dashboardId, CancellationToken ct = default) =>
        db.Dashboards.AsNoTracking().AnyAsync(d => d.Id == dashboardId && d.Status != DashboardStatus.Retired && !d.OwnershipPendingReview
            && (d.PrimaryOwnerId == userId || d.BackupOwnerId == userId), ct);

    public static Task<bool> OwnsAnyAsync(AppDbContext db, int userId, CancellationToken ct = default) =>
        db.Dashboards.AsNoTracking().AnyAsync(d => d.Status != DashboardStatus.Retired && !d.OwnershipPendingReview
            && (d.PrimaryOwnerId == userId || d.BackupOwnerId == userId), ct);
}
