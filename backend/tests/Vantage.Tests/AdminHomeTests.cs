using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The Admin Portal Home: headline numbers and things that need attention, filled only for what the person may see. Needs VANTAGE_TEST_SQL.</summary>
public class AdminHomeTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];
    private static readonly HomeScope All = new(true, true, true, true, true, true, true);
    private static readonly HomeScope None = new(false, false, false, false, false, false, false);

    private static AdminHomeService ServiceFor(AppDbContext db)
    {
        var clock = TimeProvider.System;
        return new AdminHomeService(db, new AnalyticsService(db, clock), new AuditLogService(db, clock), clock);
    }

    private static async Task<User> PersonAsync(AppDbContext db, UserType type = UserType.Internal, UserStatus status = UserStatus.Active)
    {
        var u = new User { Email = Unique("p") + "@rrd.com", DisplayName = Unique("Person "), UserType = type, Status = status, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<Dashboard> DashboardAsync(AppDbContext db, User owner, BiType type, DashboardStatus status = DashboardStatus.Active)
    {
        var clock = TimeProvider.System;
        return await new DashboardFactory(db, new OwnershipService(db, clock), new AuditWriter(db, new Ctx(), clock), clock).CreateAsync(new Dashboard
        {
            Code = "HM", Name = Unique("Home dash "), Type = type, Status = status, PrimaryOwnerId = owner.Id,
        }, null, owner.Id);
    }

    [SqlFact]
    public async Task A_person_with_no_permissions_gets_an_empty_home()
    {
        await using var db = fx.CreateContext();
        var home = await ServiceFor(db).GetAsync(None);

        Assert.Null(home.Dashboards);
        Assert.Null(home.Users);
        Assert.Null(home.Usage);
        Assert.Null(home.Requests);
        Assert.Null(home.Recent);
        Assert.Empty(home.Attention);
    }

    [SqlFact]
    public async Task The_numbers_follow_the_data_and_the_types_always_come_in_the_same_order()
    {
        await using var db = fx.CreateContext();
        var service = ServiceFor(db);
        var before = await service.GetAsync(All);
        var owner = await PersonAsync(db);
        await PersonAsync(db, UserType.External);
        await PersonAsync(db, UserType.Internal, UserStatus.Inactive);
        await DashboardAsync(db, owner, BiType.Tableau);
        await DashboardAsync(db, owner, BiType.Tableau);
        await DashboardAsync(db, owner, BiType.GenAi);
        await DashboardAsync(db, owner, BiType.PowerBi, DashboardStatus.Inactive);

        var after = await service.GetAsync(All);

        Assert.Equal(["PowerBi", "Tableau", "GenAi"], after.Dashboards!.LiveByType.Select(t => t.Key));
        int Live(AdminHome h, string type) => h.Dashboards!.LiveByType.Single(t => t.Key == type).Count;
        Assert.Equal(Live(before, "Tableau") + 2, Live(after, "Tableau"));
        Assert.Equal(Live(before, "GenAi") + 1, Live(after, "GenAi"));
        Assert.Equal(Live(before, "PowerBi"), Live(after, "PowerBi"));                      // the Inactive one is not live
        Assert.Equal(before.Dashboards!.Live + 3, after.Dashboards.Live);
        Assert.Equal(before.Dashboards.Inactive + 1, after.Dashboards.Inactive);
        Assert.Equal(before.Users!.ActiveInternal + 1, after.Users!.ActiveInternal);
        Assert.Equal(before.Users.ActiveExternal + 1, after.Users.ActiveExternal);
        Assert.Equal(before.Users.Inactive + 1, after.Users.Inactive);
        Assert.Equal(AdminHomeService.Days, after.Usage!.Days);
        Assert.Equal(AdminHomeService.Days, after.Usage.Daily.Count);                        // every day is there, even with no views
    }

    [SqlFact]
    public async Task Things_that_need_attention_are_listed_with_where_to_fix_them_and_only_when_there_are_some()
    {
        await using var db = fx.CreateContext();
        var service = ServiceFor(db);
        var before = await service.GetAsync(All);
        var owner = await PersonAsync(db);
        var orphan = await DashboardAsync(db, owner, BiType.PowerBi, DashboardStatus.Inactive);
        await db.Dashboards.Where(d => d.Id == orphan.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.OwnershipPendingReview, true));
        var flagged = await DashboardAsync(db, owner, BiType.PowerBi);
        await db.Dashboards.Where(d => d.Id == flagged.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.InactivityFlaggedAtUtc, DateTime.UtcNow));

        var after = await service.GetAsync(All);

        int Count(AdminHome h, string key) => h.Attention.SingleOrDefault(a => a.Key == key)?.Count ?? 0;
        Assert.Equal(Count(before, "no-owner") + 1, Count(after, "no-owner"));
        Assert.Equal(Count(before, "unused") + 1, Count(after, "unused"));
        Assert.Equal("/dashboards", after.Attention.Single(a => a.Key == "no-owner").Link);
        Assert.StartsWith("/analytics", after.Attention.Single(a => a.Key == "unused").Link);
        Assert.All(after.Attention, a => Assert.True(a.Count > 0));                          // nothing is listed with a count of zero
        Assert.Contains("dashboard", after.Attention.Single(a => a.Key == "no-owner").Text);

        var limited = await service.GetAsync(new HomeScope(true, false, false, false, false, false, false));
        Assert.DoesNotContain(limited.Attention, a => a.Key == "unused");                    // that needs Analytics permission
        Assert.Contains(limited.Attention, a => a.Key == "no-owner");
    }
}
