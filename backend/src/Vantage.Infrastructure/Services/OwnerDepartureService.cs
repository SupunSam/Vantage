using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;

namespace Vantage.Infrastructure.Services;

public sealed record OrphanedDashboard(int Id, string Name, string? PrimaryOwner, string? BackupOwner);

/// <summary>
/// Owner actions (approvals, access requests, group additions) are tied to the dashboard's workflow, so a dashboard with nobody
/// able to act as its owner can't stay live (C43). When neither the primary nor the backup owner is an Active user, the dashboard
/// is made Inactive and marked as waiting for new owners; only a Super Admin can fix that, by naming a new owner in Dashboards
/// Master (<see cref="DashboardMasterService"/> then makes it Active again). The portal never picks owners itself.
/// With one owner still active the dashboard stays live and the Super Admins are only told (the HRMS job does that).
/// </summary>
public sealed class OwnerDepartureService(AppDbContext db, AuditWriter audit, NotificationService notifications, EmailOutboxService email, TimeProvider clock)
{
    /// <summary>Whether a dashboard has someone who can act as its owner: at least one of the two named owners is an Active user.</summary>
    public static bool HasActiveOwner(UserStatus? primary, UserStatus? backup) => primary == UserStatus.Active || backup == UserStatus.Active;

    /// <summary>
    /// Finds every Active dashboard with no active owner, makes it Inactive, audits it and tells the Super Admins (bell and email).
    /// Safe to run any time: a dashboard already handled is no longer Active, so it isn't touched twice.
    /// </summary>
    public async Task<List<OrphanedDashboard>> ReviewAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var orphaned = await db.Dashboards.Include(d => d.PrimaryOwner).Include(d => d.BackupOwner)
            .Where(d => d.Status == DashboardStatus.Active
                        && !(d.PrimaryOwner != null && d.PrimaryOwner.Status == UserStatus.Active)
                        && !(d.BackupOwner != null && d.BackupOwner.Status == UserStatus.Active))
            .ToListAsync(ct);
        if (orphaned.Count == 0) return [];

        var admins = await db.UserRoles.Where(r => r.Role.Name == SystemRoles.SuperAdmin && r.User.Status == UserStatus.Active).Select(r => r.UserId).ToListAsync(ct);
        var result = new List<OrphanedDashboard>();
        foreach (var d in orphaned)
        {
            d.Status = DashboardStatus.Inactive;
            d.OwnershipPendingReview = true;
            d.UpdatedAtUtc = now;
            var primary = d.PrimaryOwner?.DisplayName ?? d.PrimaryOwner?.Email;
            var backup = d.BackupOwner?.DisplayName ?? d.BackupOwner?.Email;
            result.Add(new OrphanedDashboard(d.Id, d.Name, primary, backup));
            audit.Add("dashboard.inactivated-no-owner", "Dashboard", d.Id, d.Id, new { primaryOwner = d.PrimaryOwner?.Email, backupOwner = d.BackupOwner?.Email });

            var title = $"{d.Name} is Inactive: it has no owner";
            notifications.Notify(admins, "dashboard.no-owner", title, "Its owners have left or are no longer active. Name a new owner and backup owner in Dashboards Master to make it live again.", $"/dashboards/{d.Id}");
            await email.QueueAsync(admins, "dashboard.no-owner", title, "A dashboard has no owner",
                $"<p><strong>{EmailOutboxService.Encode(d.Name)}</strong> has been made Inactive because neither its primary owner nor its backup owner is an active user.</p>"
                + "<p>Owner approvals and access requests depend on an owner, so it stays Inactive until you name a new owner (and a backup owner) in Dashboards Master. It becomes Active again as soon as you save the new owner.</p>",
                "Open the Dashboard", $"{email.Options.AdminPortalUrl}/dashboards/{d.Id}", ct);
        }
        await db.SaveChangesAsync(ct);
        return result;
    }
}
