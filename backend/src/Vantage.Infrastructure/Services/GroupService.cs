using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Dashboard groups. Rule agreed on 3 Oct 2026: only dashboards with RLS = Yes can have more than the default group,
/// because without RLS every member sees the same data. The RLS value stays visible and editable on every group;
/// on a dashboard without RLS it is simply not sent in the embed token.
/// </summary>
public sealed class GroupService(AppDbContext db, AuditWriter audit, TimeProvider clock)
{
    public const string NoRlsMessage =
        "This dashboard doesn't use row-level security, so everyone in it sees the same data and it only needs its default group. Add people to the default group instead.";

    public async Task<DashboardGroup> AddAsync(int dashboardId, string? name, string? rlsValue, int actorUserId, CancellationToken ct = default)
    {
        var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.Id == dashboardId, ct) ?? throw new KeyNotFoundException();
        if (dashboard.Status == DashboardStatus.Retired) throw new RuleException("This dashboard is retired.");
        if (!dashboard.RlsEnabled) throw new RuleException(NoRlsMessage);

        var clean = (name ?? "").Trim();
        if (!Rules.IsValidGroupName(clean)) throw new RuleException("Group names are 1 to 40 letters, digits, hyphens or underscores.");
        if (await db.DashboardGroups.AnyAsync(g => g.Name == clean, ct)) throw new RuleException($"The group name '{clean}' is already used.");
        var rls = CleanRls(rlsValue);
        if (rls is null) throw new RuleException("This dashboard uses row-level security, so the new group needs an RLS value: a role name from the report.");

        var group = new DashboardGroup { DashboardId = dashboardId, Name = clean, RlsValue = rls, CreatedAtUtc = clock.GetUtcNow().UtcDateTime, CreatedByUserId = actorUserId };
        db.DashboardGroups.Add(group);
        await db.SaveChangesAsync(ct);
        audit.Add("group.created", "DashboardGroup", group.Id, dashboardId, new { group.Name, group.RlsValue });
        await db.SaveChangesAsync(ct);
        return group;
    }

    /// <summary>Changes a group's RLS value (one or more report role names, comma-separated). Allowed on any dashboard.</summary>
    public async Task<DashboardGroup> SetRlsAsync(int dashboardId, int groupId, string? rlsValue, CancellationToken ct = default)
    {
        var group = await db.DashboardGroups.Include(g => g.Dashboard).SingleOrDefaultAsync(g => g.Id == groupId && g.DashboardId == dashboardId, ct)
            ?? throw new KeyNotFoundException();
        var value = CleanRls(rlsValue);
        if (value is null && group.Dashboard.RlsEnabled)
            throw new RuleException("This dashboard uses row-level security, so the group needs an RLS value. Without one, its members can't open the dashboard.");
        if (value == group.RlsValue) return group;
        audit.Add("group.rls-changed", "DashboardGroup", groupId, dashboardId, new { from = group.RlsValue, to = value });
        group.RlsValue = value;
        await db.SaveChangesAsync(ct);
        return group;
    }

    private static string? CleanRls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var roles = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var clean = string.Join(",", roles);
        if (clean.Length == 0) return null;
        if (clean.Length > Rules.RlsValueMax) throw new RuleException($"The RLS value is up to {Rules.RlsValueMax} characters.");
        return clean;
    }
}
