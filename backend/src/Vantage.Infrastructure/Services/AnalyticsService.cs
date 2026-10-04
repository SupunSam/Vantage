using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

public sealed record DayPoint(DateOnly Day, int Views, int Users);
public sealed record TopDashboard(int Id, string Name, string Type, int Views, int Users, DateTime? LastViewedAtUtc);
public sealed record TopUser(int Id, string Name, string Email, int Views, int Dashboards, DateTime? LastViewedAtUtc);
public sealed record TypeShare(string Type, int Views, int Dashboards);
public sealed record UnusedDashboard(int Id, string Name, string Type, string? Owner, DateTime? LastViewedAtUtc, int Members);

/// <param name="InScope">Active dashboards the numbers cover (all of them for a Super Admin, the owner's own for an owner).</param>
public sealed record AnalyticsOverview(
    int Days, DateTime FromUtc, int Views, int UniqueUsers, int InScope, int Viewed, int Unused,
    List<DayPoint> Daily, List<TopDashboard> TopDashboards, List<TopUser> TopUsers, List<TypeShare> ByType, List<UnusedDashboard> UnusedList);

public sealed record DashboardUsage(
    int Id, string Code, string Name, string Type, string Status, string? Owner, string? BackupOwner, int Views, int Users, DateTime? LastViewedAtUtc,
    int Members, bool Flagged, int? DaysSinceView);

public sealed record DashboardUsageDetail(
    int Id, string Name, string Type, string Status, int Days, int Views, int Users, int Members, int MembersNeverOpened,
    List<DayPoint> Daily, List<TopUser> TopUsers);

public sealed record AccessPerson(int UserId, string Email, string? Name, string UserType, string UserStatus, int GroupId, string Group, string GroupStatus, string? RlsValue, DateTime AddedAtUtc, bool CanOpenNow);
public sealed record WhoCanOpen(int DashboardId, string Dashboard, string Status, bool RlsEnabled, List<AccessPerson> People);
public sealed record AccessDashboard(int DashboardId, string Dashboard, string Code, string Type, string Status, bool RlsEnabled, int GroupId, string Group, string GroupStatus, string? RlsValue, DateTime AddedAtUtc, bool CanOpenNow);
public sealed record WhatCanOpen(int UserId, string Email, string? Name, string UserStatus, List<AccessDashboard> Dashboards);

/// <summary>
/// Analytics (C30). Usage comes from the views the portal records when someone opens a dashboard; previews by Super
/// Admins are not views. The same table takes Power BI activity and Tableau usage imports later (their own sources).
/// The access reports answer "who can open dashboard X" and "what can user Y open" from current group memberships.
/// </summary>
public sealed class AnalyticsService(AppDbContext db, TimeProvider clock)
{
    public const int MaxDays = 730;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static int ClampDays(int days) => Math.Clamp(days, 1, MaxDays);

    /// <summary>Ids of the dashboards a person owns (primary or backup), for owner analytics.</summary>
    public async Task<List<int>> OwnedAsync(int userId, CancellationToken ct = default) =>
        await db.Dashboards.AsNoTracking().Where(d => d.PrimaryOwnerId == userId || d.BackupOwnerId == userId).Select(d => d.Id).ToListAsync(ct);

    /// <summary>
    /// "PowerBi", "Tableau" or "GenAi" (any case) as a type, or null for "all types" (blank). Anything else is refused, so a typo can't quietly show everything.
    /// </summary>
    public static DashboardType? ParseType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var t = type.Trim().Replace(" ", "");
        // An exact name match: Enum.TryParse would also accept numbers and comma lists ("PowerBi,Tableau").
        var name = Enum.GetNames<DashboardType>().FirstOrDefault(n => n.Equals(t, StringComparison.OrdinalIgnoreCase));
        if (name is not null) return Enum.Parse<DashboardType>(name);
        throw new RuleException("Dashboard type must be Power BI, Tableau or GenAI, or blank for all types.");
    }

    /// <summary>
    /// The dashboards an analytics view covers once a type is chosen: null for everything (no type, no owner scope), otherwise the ids that
    /// are of that type and, for an owner, also owned by them. An empty list means "nothing matches", not "everything".
    /// </summary>
    public async Task<List<int>?> ScopeAsync(string? type, IReadOnlyCollection<int>? within, CancellationToken ct = default)
    {
        var t = ParseType(type);
        if (t is null) return within?.ToList();
        var q = db.Dashboards.AsNoTracking().Where(d => d.Type == t);
        if (within is not null) q = q.Where(d => within.Contains(d.Id));
        return await q.Select(d => d.Id).ToListAsync(ct);
    }

    private IQueryable<DashboardView> Views(DateTime from, IReadOnlyCollection<int>? scope)
    {
        var q = db.DashboardViews.AsNoTracking().Where(v => v.ViewedAtUtc >= from);
        return scope is null ? q : q.Where(v => scope.Contains(v.DashboardId));
    }

    private IQueryable<Dashboard> ActiveDashboards(IReadOnlyCollection<int>? scope)
    {
        var q = db.Dashboards.AsNoTracking().Where(d => d.Status == DashboardStatus.Active);
        return scope is null ? q : q.Where(d => scope.Contains(d.Id));
    }

    private static List<DayPoint> FillDays(DateTime from, DateTime now, IEnumerable<DayPoint> found)
    {
        var byDay = found.ToDictionary(p => p.Day);
        var list = new List<DayPoint>();
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(now); day = day.AddDays(1))
            list.Add(byDay.TryGetValue(day, out var p) ? p : new DayPoint(day, 0, 0));
        return list;
    }

    private async Task<List<DayPoint>> DailyAsync(DateTime from, IQueryable<DashboardView> views, CancellationToken ct)
    {
        var rows = await views.GroupBy(v => v.ViewedAtUtc.Date)
            .Select(g => new { Day = g.Key, Views = g.Count(), Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count() }).ToListAsync(ct);
        return FillDays(from, Now, rows.Select(r => new DayPoint(DateOnly.FromDateTime(r.Day), r.Views, r.Users)));
    }

    // ------------------------------------------------------------ Overview

    /// <param name="scope">Null for every dashboard (Super Admin), or the dashboards to cover (an owner's own).</param>
    public async Task<AnalyticsOverview> OverviewAsync(int days, IReadOnlyCollection<int>? scope, CancellationToken ct = default)
    {
        days = ClampDays(days);
        var from = Now.Date.AddDays(-(days - 1));
        var views = Views(from, scope);

        var totals = await views.GroupBy(_ => 1).Select(g => new { Views = g.Count(), Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count() }).SingleOrDefaultAsync(ct);
        var daily = await DailyAsync(from, views, ct);

        var perDashboard = await views.GroupBy(v => v.DashboardId)
            .Select(g => new { Id = g.Key, Views = g.Count(), Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count() }).ToListAsync(ct);
        var info = await db.Dashboards.AsNoTracking().Where(d => perDashboard.Select(p => p.Id).Contains(d.Id))
            .Select(d => new { d.Id, d.Name, Type = d.Type.ToString(), d.LastViewedAtUtc }).ToDictionaryAsync(d => d.Id, ct);
        var top = perDashboard.OrderByDescending(p => p.Views).ThenBy(p => info[p.Id].Name).Take(10)
            .Select(p => new TopDashboard(p.Id, info[p.Id].Name, info[p.Id].Type, p.Views, p.Users, info[p.Id].LastViewedAtUtc)).ToList();

        var byType = perDashboard.GroupBy(p => info[p.Id].Type).Select(g => new TypeShare(g.Key, g.Sum(x => x.Views), g.Count())).OrderByDescending(t => t.Views).ToList();

        var perUser = await views.Where(v => v.UserId != null).GroupBy(v => v.UserId!.Value)
            .Select(g => new { Id = g.Key, Views = g.Count(), Dashboards = g.Select(x => x.DashboardId).Distinct().Count(), Last = g.Max(x => x.ViewedAtUtc) })
            .OrderByDescending(x => x.Views).Take(10).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().Where(u => perUser.Select(p => p.Id).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.DisplayName, u.Email }, ct);
        var topUsers = perUser.Select(p => new TopUser(p.Id, names[p.Id].DisplayName ?? names[p.Id].Email, names[p.Id].Email, p.Views, p.Dashboards, p.Last)).ToList();

        var viewedIds = perDashboard.Select(p => p.Id).ToList();
        var active = ActiveDashboards(scope);
        var inScope = await active.CountAsync(ct);
        var viewed = await active.CountAsync(d => viewedIds.Contains(d.Id), ct);
        var unused = await active.Where(d => !viewedIds.Contains(d.Id)).OrderBy(d => d.LastViewedAtUtc ?? DateTime.MinValue).ThenBy(d => d.Name).Take(20)
            .Select(d => new UnusedDashboard(d.Id, d.Name, d.Type.ToString(), d.PrimaryOwner != null ? d.PrimaryOwner.DisplayName ?? d.PrimaryOwner.Email : null, d.LastViewedAtUtc,
                db.GroupMembers.Count(m => m.DashboardId == d.Id && m.RemovedAtUtc == null))).ToListAsync(ct);

        return new AnalyticsOverview(days, from, totals?.Views ?? 0, totals?.Users ?? 0, inScope, viewed, inScope - viewed, daily, top, topUsers, byType, unused);
    }

    // ------------------------------------------------------------ Dashboards

    public async Task<List<DashboardUsage>> DashboardsAsync(int days, IReadOnlyCollection<int>? scope, CancellationToken ct = default)
    {
        days = ClampDays(days);
        var from = Now.Date.AddDays(-(days - 1));
        var counts = await Views(from, scope).GroupBy(v => v.DashboardId)
            .Select(g => new { Id = g.Key, Views = g.Count(), Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count() }).ToDictionaryAsync(x => x.Id, ct);
        var q = db.Dashboards.AsNoTracking().Where(d => d.Status != DashboardStatus.Retired);
        if (scope is not null) q = q.Where(d => scope.Contains(d.Id));
        var rows = await q.OrderBy(d => d.Name).Select(d => new
        {
            d.Id, d.Code, d.Name, Type = d.Type.ToString(), Status = d.Status.ToString(),
            Owner = d.PrimaryOwner != null ? d.PrimaryOwner.DisplayName ?? d.PrimaryOwner.Email : null,
            Backup = d.BackupOwner != null ? d.BackupOwner.DisplayName ?? d.BackupOwner.Email : null,
            d.LastViewedAtUtc, Flagged = d.InactivityFlaggedAtUtc != null,
            Members = db.GroupMembers.Count(m => m.DashboardId == d.Id && m.RemovedAtUtc == null),
        }).ToListAsync(ct);
        return rows.Select(r => new DashboardUsage(r.Id, r.Code, r.Name, r.Type, r.Status, r.Owner, r.Backup,
            counts.TryGetValue(r.Id, out var c) ? c.Views : 0, counts.TryGetValue(r.Id, out c) ? c.Users : 0, r.LastViewedAtUtc, r.Members, r.Flagged,
            r.LastViewedAtUtc is { } last ? (int)(Now - last).TotalDays : null)).ToList();
    }

    public async Task<DashboardUsageDetail> DashboardAsync(int dashboardId, int days, IReadOnlyCollection<int>? scope, CancellationToken ct = default)
    {
        if (scope is not null && !scope.Contains(dashboardId)) throw new KeyNotFoundException();
        days = ClampDays(days);
        var from = Now.Date.AddDays(-(days - 1));
        var d = await db.Dashboards.AsNoTracking().Where(x => x.Id == dashboardId).Select(x => new { x.Id, x.Name, Type = x.Type.ToString(), Status = x.Status.ToString() }).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException();
        var views = Views(from, [dashboardId]);
        var totals = await views.GroupBy(_ => 1).Select(g => new { Views = g.Count(), Users = g.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count() }).SingleOrDefaultAsync(ct);
        var daily = await DailyAsync(from, views, ct);

        var perUser = await views.Where(v => v.UserId != null).GroupBy(v => v.UserId!.Value)
            .Select(g => new { Id = g.Key, Views = g.Count(), Last = g.Max(x => x.ViewedAtUtc) }).OrderByDescending(x => x.Views).Take(10).ToListAsync(ct);
        var names = await db.Users.AsNoTracking().Where(u => perUser.Select(p => p.Id).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.DisplayName, u.Email }, ct);

        var members = await db.GroupMembers.AsNoTracking().Where(m => m.DashboardId == dashboardId && m.RemovedAtUtc == null).Select(m => m.UserId).Distinct().ToListAsync(ct);
        var viewers = await views.Where(v => v.UserId != null).Select(v => v.UserId!.Value).Distinct().ToListAsync(ct);
        return new DashboardUsageDetail(d.Id, d.Name, d.Type, d.Status, days, totals?.Views ?? 0, totals?.Users ?? 0, members.Count, members.Except(viewers).Count(), daily,
            perUser.Select(p => new TopUser(p.Id, names[p.Id].DisplayName ?? names[p.Id].Email, names[p.Id].Email, p.Views, 1, p.Last)).ToList());
    }

    // ------------------------------------------------------------ Access reports

    /// <summary>Everyone with a live place in a group of the dashboard, and whether they can open it right now.</summary>
    public async Task<WhoCanOpen> WhoCanOpenAsync(int dashboardId, CancellationToken ct = default)
    {
        var d = await db.Dashboards.AsNoTracking().Where(x => x.Id == dashboardId).Select(x => new { x.Id, x.Name, x.Status, x.RlsEnabled }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        var rows = await db.GroupMembers.AsNoTracking().Where(m => m.DashboardId == dashboardId && m.RemovedAtUtc == null)
            .OrderBy(m => m.Group.Name).ThenBy(m => m.User.DisplayName ?? m.User.Email)
            .Select(m => new
            {
                m.UserId, m.User.Email, m.User.DisplayName, UserType = m.User.UserType.ToString(), UserStatus = m.User.Status, m.GroupId, Group = m.Group.Name,
                GroupStatus = m.Group.Status, m.Group.RlsValue, m.AddedAtUtc,
            }).ToListAsync(ct);
        var people = rows.Select(r => new AccessPerson(r.UserId, r.Email, r.DisplayName, r.UserType, r.UserStatus.ToString(), r.GroupId, r.Group, r.GroupStatus.ToString(),
            d.RlsEnabled ? r.RlsValue : null, r.AddedAtUtc, d.Status == DashboardStatus.Active && r.GroupStatus == GroupStatus.Active && r.UserStatus != UserStatus.Inactive)).ToList();
        return new WhoCanOpen(d.Id, d.Name, d.Status.ToString(), d.RlsEnabled, people);
    }

    /// <summary>Every dashboard the person has a live place for, and whether they can open it right now.</summary>
    public async Task<WhatCanOpen> WhatCanOpenAsync(int userId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.Id, x.Email, x.DisplayName, x.Status }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        var rows = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == userId && m.RemovedAtUtc == null && m.Group.Dashboard.Status != DashboardStatus.Retired)
            .OrderBy(m => m.Group.Dashboard.Name)
            .Select(m => new
            {
                m.DashboardId, Dashboard = m.Group.Dashboard.Name, m.Group.Dashboard.Code, Type = m.Group.Dashboard.Type.ToString(), DashboardStatus = m.Group.Dashboard.Status,
                m.Group.Dashboard.RlsEnabled, m.GroupId, Group = m.Group.Name, GroupStatus = m.Group.Status, m.Group.RlsValue, m.AddedAtUtc,
            }).ToListAsync(ct);
        var list = rows.Select(r => new AccessDashboard(r.DashboardId, r.Dashboard, r.Code, r.Type, r.DashboardStatus.ToString(), r.RlsEnabled, r.GroupId, r.Group, r.GroupStatus.ToString(),
            r.RlsEnabled ? r.RlsValue : null, r.AddedAtUtc, r.DashboardStatus == DashboardStatus.Active && r.GroupStatus == GroupStatus.Active && u.Status != UserStatus.Inactive)).ToList();
        return new WhatCanOpen(u.Id, u.Email, u.DisplayName, u.Status.ToString(), list);
    }
}
