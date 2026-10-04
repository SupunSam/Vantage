using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>Which parts of the Admin Home the signed-in person may see (from their module permissions).</summary>
public sealed record HomeScope(bool Dashboards, bool Users, bool Groups, bool Analytics, bool Audit, bool Jobs, bool Tenants);

public sealed record CountBy(string Key, int Count);
public sealed record HomeDashboards(int Live, int Inactive, int NotLive, List<CountBy> LiveByType);
public sealed record HomeUsers(int ActiveInternal, int ActiveExternal, int Inactive, int SetupPending, int AccessGroups);
public sealed record HomeUsage(int Days, int Views, int UniqueUsers, int InScope, int Viewed, int Unused, List<DayPoint> Daily, List<TopDashboard> Top);
public sealed record HomeRequests(int AccessRequests, int GroupAdditions);
/// <param name="Link">Where in the Admin Portal to deal with it.</param>
public sealed record AttentionItem(string Key, string Text, int Count, string Link);
public sealed record HomeActivity(DateTime OccurredAtUtc, string? Actor, string Description);

/// <summary>Each part is null when the person has no permission for it, so the page only shows what they may see.</summary>
public sealed record AdminHome(HomeDashboards? Dashboards, HomeUsers? Users, HomeUsage? Usage, HomeRequests? Requests, List<AttentionItem> Attention, List<HomeActivity>? Recent);

/// <summary>
/// The Admin Portal's Home page: a few headline numbers, the last 30 days of usage, and a short list of things that need a
/// Super Admin's attention. It reads what other modules already keep; nothing here is stored.
/// </summary>
public sealed class AdminHomeService(AppDbContext db, AnalyticsService analytics, AuditLogService audit, TimeProvider clock)
{
    public const int Days = 30;
    public const int TopCount = 5;
    public const int RecentCount = 8;

    public async Task<AdminHome> GetAsync(HomeScope scope, CancellationToken ct = default)
    {
        HomeDashboards? dashboards = null;
        if (scope.Dashboards)
        {
            var byStatus = await db.Dashboards.AsNoTracking().GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            int Of(DashboardStatus s) => byStatus.Where(x => x.Key == s).Sum(x => x.N);
            var liveByType = await db.Dashboards.AsNoTracking().Where(d => d.Status == DashboardStatus.Active).GroupBy(d => d.Type).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            dashboards = new HomeDashboards(Of(DashboardStatus.Active), Of(DashboardStatus.Inactive),
                Of(DashboardStatus.Draft) + Of(DashboardStatus.Publishing) + Of(DashboardStatus.Failed),
                // Always the same three types in the same order, so a chart never repaints or reorders its colours.
                Enum.GetValues<DashboardType>().Select(t => new CountBy(t.ToString(), liveByType.Where(x => x.Key == t).Sum(x => x.N))).ToList());
        }

        HomeUsers? users = null;
        if (scope.Users || scope.Groups)
        {
            var rows = await db.Users.AsNoTracking().GroupBy(u => new { u.UserType, u.Status }).Select(g => new { g.Key.UserType, g.Key.Status, N = g.Count() }).ToListAsync(ct);
            int U(UserType t, UserStatus s) => rows.Where(r => r.UserType == t && r.Status == s).Sum(r => r.N);
            users = new HomeUsers(U(UserType.Internal, UserStatus.Active), U(UserType.External, UserStatus.Active),
                rows.Where(r => r.Status == UserStatus.Inactive).Sum(r => r.N), rows.Where(r => r.Status == UserStatus.PendingSetup).Sum(r => r.N),
                await db.DashboardGroups.CountAsync(g => g.Status == GroupStatus.Active, ct));
        }

        HomeUsage? usage = null;
        if (scope.Analytics)
        {
            var o = await analytics.OverviewAsync(Days, null, ct);
            usage = new HomeUsage(o.Days, o.Views, o.UniqueUsers, o.InScope, o.Viewed, o.Unused, o.Daily, o.TopDashboards.Take(TopCount).ToList());
        }

        HomeRequests? requests = null;
        if (scope.Groups)
            requests = new HomeRequests(await db.AccessRequests.CountAsync(r => r.Status == AccessRequestStatus.Pending, ct),
                await db.GroupAddRequests.CountAsync(r => r.Status == GroupAddRequestStatus.Pending, ct));

        var attention = new List<AttentionItem>();
        void Add(string key, string singular, string plural, int n, string link) { if (n > 0) attention.Add(new AttentionItem(key, n == 1 ? singular : plural.Replace("{n}", n.ToString()), n, link)); }
        if (scope.Dashboards)
        {
            Add("no-owner", "1 dashboard has no owner and is Inactive", "{n} dashboards have no owner and are Inactive",
                await db.Dashboards.CountAsync(d => d.Status == DashboardStatus.Inactive && d.OwnershipPendingReview, ct), "/dashboards");
            Add("no-category", "1 dashboard has no category", "{n} dashboards have no category",
                await db.Dashboards.CountAsync(d => d.Status != DashboardStatus.Retired && d.CategoryId == null, ct), "/dashboards");
        }
        if (scope.Analytics)
            Add("unused", "1 dashboard is flagged as unused", "{n} dashboards are flagged as unused",
                await db.Dashboards.CountAsync(d => d.Status == DashboardStatus.Active && d.InactivityFlaggedAtUtc != null, ct), "/analytics?tab=dashboards");
        if (requests is not null)
            Add("requests", "1 request is waiting for a decision", "{n} requests are waiting for a decision", requests.AccessRequests + requests.GroupAdditions, "/requests");
        if (scope.Tenants)
            Add("tenant", "1 tenant has not passed Verify", "{n} tenants have not passed Verify",
                await db.BiTenants.CountAsync(t => t.IsActive && t.LastVerifyPassed != true, ct), "/tenants");
        if (scope.Jobs)
        {
            var latest = await db.JobRuns.AsNoTracking().Where(r => r.StartedAtUtc == db.JobRuns.Where(x => x.JobName == r.JobName).Max(x => x.StartedAtUtc)).Select(r => r.Status).ToListAsync(ct);
            Add("job", "1 scheduled job failed on its last run", "{n} scheduled jobs failed on their last run", latest.Count(s => s == JobRunStatus.Failed), "/jobs");
        }

        List<HomeActivity>? recent = null;
        if (scope.Audit)
        {
            var page = await audit.SearchAsync(new AuditQuery(null, null, null, null, null, null, null, null, 1, RecentCount), ct);
            recent = page.Rows.Select(r => new HomeActivity(r.OccurredAtUtc, r.Actor, r.Description)).ToList();
        }

        return new AdminHome(dashboards, users, usage, requests, attention, recent);
    }
}
