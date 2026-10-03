using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Creates a dashboard together with its default group (&lt;code&gt;-&lt;id&gt;-default), adds both owners to it,
/// and syncs their Dashboard Owner role, all in one transaction.
/// </summary>
public sealed class DashboardFactory(AppDbContext db, OwnershipService ownership, AuditWriter audit, TimeProvider clock)
{
    public async Task<Dashboard> CreateAsync(Dashboard dashboard, string? defaultGroupRlsValue, int? actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dashboard.Name) || dashboard.Name.Length > Rules.DashboardNameMax)
            throw new ArgumentException($"Dashboard name is required, up to {Rules.DashboardNameMax} characters.");
        if (!Rules.IsValidDashboardCode(dashboard.Code))
            throw new ArgumentException("Dashboard code must be 1 to 20 letters, digits, hyphens or underscores.");
        if (dashboard.RlsEnabled && string.IsNullOrWhiteSpace(defaultGroupRlsValue))
            throw new ArgumentException("RLS is on, so the default group needs an RLS value.");
        if (dashboard.PrimaryOwnerId is null)
            throw new ArgumentException("A primary owner is required.");
        if (await db.Dashboards.AnyAsync(d => d.Name == dashboard.Name, ct))
            throw new ArgumentException($"A dashboard named '{dashboard.Name}' already exists.");

        // SQL Server retry-on-failure needs user transactions wrapped in the execution strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            dashboard.CreatedAtUtc = now;
            dashboard.CreatedByUserId = actorUserId;
            db.Dashboards.Add(dashboard);
            await db.SaveChangesAsync(ct); // need the ID for the default group name

            var group = new DashboardGroup
            {
                DashboardId = dashboard.Id,
                Name = Rules.DefaultGroupName(dashboard.Code, dashboard.Id),
                RlsValue = string.IsNullOrWhiteSpace(defaultGroupRlsValue) ? null : defaultGroupRlsValue.Trim(),
                IsDefault = true,
                CreatedAtUtc = now,
                CreatedByUserId = actorUserId,
            };
            db.DashboardGroups.Add(group);
            await db.SaveChangesAsync(ct);

            foreach (var ownerId in new[] { dashboard.PrimaryOwnerId, dashboard.BackupOwnerId }.OfType<int>().Distinct())
            {
                db.GroupMembers.Add(new GroupMember
                {
                    GroupId = group.Id, DashboardId = dashboard.Id, UserId = ownerId,
                    Source = MembershipSource.OwnerAuto, AddedAtUtc = now, AddedByUserId = actorUserId,
                });
                await ownership.SyncOwnerRoleAsync(ownerId, ct);
            }

            audit.Add("dashboard.created", "Dashboard", dashboard.Id, dashboard.Id,
                new { dashboard.Name, dashboard.Code, type = dashboard.Type.ToString(), defaultGroup = group.Name });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return dashboard;
    }
}
