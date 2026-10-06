using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Analytics: usage numbers from recorded views, owner scoping, and the two access reports.</summary>
public class AnalyticsServiceTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static readonly DateTimeOffset Today = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, AnalyticsService Analytics, DashboardFactory Factory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Kit Arrange()
    {
        var db = fx.CreateContext();
        var clock = new FakeTimeProvider(Today);
        return new Kit(db, new AnalyticsService(db, clock), new DashboardFactory(db, new OwnershipService(db, clock), new AuditWriter(db, new Ctx(), clock), clock));
    }

    private static async Task<User> UserAsync(AppDbContext db, UserStatus status = UserStatus.Active)
    {
        var u = new User { Email = Unique("u") + "@rrd.com", DisplayName = Unique("User "), UserType = UserType.Internal, Status = status, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<Dashboard> DashboardAsync(Kit k, User owner, bool rls = false, DashboardStatus status = DashboardStatus.Active)
    {
        var d = await k.Factory.CreateAsync(new Dashboard
        {
            Code = "AN", Name = Unique("Dash "), Type = BiType.PowerBi, Status = status, RlsEnabled = rls, PrimaryOwnerId = owner.Id,
        }, rls ? "Region_All" : null, owner.Id);
        return d;
    }

    private static void View(Kit k, Dashboard d, User? u, double daysAgo, ViewSource source = ViewSource.Portal) =>
        k.Db.DashboardViews.Add(new DashboardView { DashboardId = d.Id, UserId = u?.Id, Source = source, ViewedAtUtc = Today.UtcDateTime.AddDays(-daysAgo) });

    [SqlFact]
    public async Task Overview_counts_views_and_unique_people_in_the_range_and_fills_every_day()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var a = await DashboardAsync(k, owner);
        var b = await DashboardAsync(k, owner);
        var unused = await DashboardAsync(k, owner);
        var p1 = await UserAsync(k.Db); var p2 = await UserAsync(k.Db);
        View(k, a, p1, 0.2); View(k, a, p1, 0.3); View(k, a, p2, 1); View(k, a, p2, 2);     // a: 4 views, 2 people
        View(k, b, p1, 5);                                                                  // b: 1 view
        View(k, a, p1, 40);                                                                 // outside a 30-day range
        a.LastViewedAtUtc = Today.UtcDateTime.AddDays(-0.2);
        await k.Db.SaveChangesAsync();
        int[] scope = [a.Id, b.Id, unused.Id];

        var o = await k.Analytics.OverviewAsync(30, scope);

        Assert.Equal(5, o.Views);
        Assert.Equal(2, o.UniqueUsers);
        Assert.Equal(3, o.InScope);
        Assert.Equal(2, o.Viewed);
        Assert.Equal(1, o.Unused);
        Assert.Equal(unused.Id, o.UnusedList.Single().Id);
        Assert.Equal(30, o.Daily.Count);                                                    // a point for every day, views or not
        Assert.Equal(new DateOnly(2026, 10, 4), o.Daily[^1].Day);
        Assert.Equal(5, o.Daily.Sum(d => d.Views));
        Assert.Equal([a.Id, b.Id], o.TopDashboards.Select(t => t.Id));                      // most viewed first
        Assert.Equal(4, o.TopDashboards[0].Views);
        Assert.Equal(2, o.TopDashboards[0].Users);
        Assert.Equal(p1.Id, o.TopUsers[0].Id);
        Assert.Equal(3, o.TopUsers[0].Views);                                               // p1: 2 on a and 1 on b in range (the 40-day-old view is outside)
        Assert.Equal(3, (await k.Analytics.OverviewAsync(2, scope)).Views);                // today and yesterday only: three of the four views on a
    }

    [SqlFact]
    public async Task Owners_only_see_their_own_dashboards()
    {
        await using var k = Arrange();
        var mine = await UserAsync(k.Db); var other = await UserAsync(k.Db); var viewer = await UserAsync(k.Db);
        var theirs = await DashboardAsync(k, mine);
        var notTheirs = await DashboardAsync(k, other);
        View(k, theirs, viewer, 1); View(k, notTheirs, viewer, 1); View(k, notTheirs, viewer, 2);
        await k.Db.SaveChangesAsync();

        var owned = await k.Analytics.OwnedAsync(mine.Id);
        Assert.Equal([theirs.Id], owned);
        var o = await k.Analytics.OverviewAsync(30, owned);
        Assert.Equal(1, o.Views);
        Assert.Equal([theirs.Id], (await k.Analytics.DashboardsAsync(30, owned)).Select(d => d.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => k.Analytics.DashboardAsync(notTheirs.Id, 30, owned));
        Assert.Equal(1, (await k.Analytics.DashboardAsync(theirs.Id, 30, owned)).Views);
        Assert.Empty(await k.Analytics.OwnedAsync(viewer.Id));                              // someone who owns nothing has an empty scope
    }

    [SqlFact]
    public async Task Dashboard_detail_shows_who_uses_it_and_which_members_never_opened_it()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner);
        var user1 = await UserAsync(k.Db); var user2 = await UserAsync(k.Db);
        var access = new AccessGroupService(k.Db, new GroupService(k.Db, new AuditWriter(k.Db, new Ctx(), TimeProvider.System), TimeProvider.System),
            new NotificationService(k.Db, TimeProvider.System), new AuditWriter(k.Db, new Ctx(), TimeProvider.System), TimeProvider.System);
        var def = await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault);
        await access.AddMembersAsync(def.Id, [user1.Email, user2.Email], false, MembershipSource.Manual, new GroupActor(owner.Id, true));
        View(k, d, user1, 1); View(k, d, user1, 2); View(k, d, null, 1);                    // an import with no user still counts as a view
        await k.Db.SaveChangesAsync();

        var detail = await k.Analytics.DashboardAsync(d.Id, 30, null);

        Assert.Equal(3, detail.Views);
        Assert.Equal(1, detail.Users);
        Assert.Equal(3, detail.Members);                                                    // the owner and the two people
        Assert.Equal(3 - 1, detail.MembersNeverOpened);                                     // only user1 opened it
        Assert.Equal(user1.Id, detail.TopUsers.Single().Id);
        Assert.Equal(2, detail.TopUsers.Single().Views);
    }

    [SqlFact]
    public async Task Who_can_open_lists_members_by_group_and_marks_who_really_can_right_now()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner, rls: true);
        var north = (await k.Db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = d.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow })).Entity;
        var paused = (await k.Db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = d.Id, Name = Unique("P"), RlsValue = "Region_East", Status = GroupStatus.Inactive, CreatedAtUtc = DateTime.UtcNow })).Entity;
        var ok = await UserAsync(k.Db); var inPausedGroup = await UserAsync(k.Db); var leaver = await UserAsync(k.Db, UserStatus.Inactive); var removed = await UserAsync(k.Db);
        await k.Db.SaveChangesAsync();
        foreach (var (u, g) in new[] { (ok, north), (inPausedGroup, paused), (leaver, north), (removed, north) })
            k.Db.GroupMembers.Add(new GroupMember { GroupId = g.Id, DashboardId = d.Id, UserId = u.Id, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow, RemovedAtUtc = u == removed ? DateTime.UtcNow : null });
        await k.Db.SaveChangesAsync();

        var r = await k.Analytics.WhoCanOpenAsync(d.Id);

        Assert.Equal(4, r.People.Count);                                                    // the owner, ok, the paused-group member and the leaver; not the removed one
        Assert.DoesNotContain(r.People, p => p.UserId == removed.Id);
        Assert.True(r.People.Single(p => p.UserId == ok.Id).CanOpenNow);
        Assert.Equal("Region_North", r.People.Single(p => p.UserId == ok.Id).RlsValue);
        Assert.False(r.People.Single(p => p.UserId == inPausedGroup.Id).CanOpenNow);        // the group is inactive
        Assert.False(r.People.Single(p => p.UserId == leaver.Id).CanOpenNow);               // the user is inactive
        Assert.True(r.People.Single(p => p.UserId == owner.Id).CanOpenNow);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => k.Analytics.WhoCanOpenAsync(-1));
    }

    [SqlFact]
    public async Task What_can_open_lists_a_persons_dashboards_and_hides_the_rls_value_when_there_is_no_rls()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var person = await UserAsync(k.Db);
        var plain = await DashboardAsync(k, owner, rls: false);
        var secured = await DashboardAsync(k, owner, rls: true);
        var retired = await DashboardAsync(k, owner, status: DashboardStatus.Retired);
        var inactive = await DashboardAsync(k, owner, status: DashboardStatus.Inactive);
        foreach (var d in new[] { plain, secured, retired, inactive })
        {
            var g = await k.Db.DashboardGroups.FirstAsync(x => x.DashboardId == d.Id);
            if (d.Id == plain.Id) g.RlsValue = "Ignored";
            k.Db.GroupMembers.Add(new GroupMember { GroupId = g.Id, DashboardId = d.Id, UserId = person.Id, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow });
        }
        await k.Db.SaveChangesAsync();

        var r = await k.Analytics.WhatCanOpenAsync(person.Id);

        Assert.Equal(3, r.Dashboards.Count);                                                // retired dashboards are left out
        Assert.True(r.Dashboards.Single(d => d.DashboardId == plain.Id).CanOpenNow);
        Assert.Null(r.Dashboards.Single(d => d.DashboardId == plain.Id).RlsValue);          // no RLS: the stored value isn't used
        Assert.Equal("Region_All", r.Dashboards.Single(d => d.DashboardId == secured.Id).RlsValue);
        Assert.False(r.Dashboards.Single(d => d.DashboardId == inactive.Id).CanOpenNow);    // the dashboard itself is inactive
        Assert.DoesNotContain(r.Dashboards, d => d.DashboardId == retired.Id);
    }
}
