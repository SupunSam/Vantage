using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Pure rules: who counts as an owner who can act, and the analytics type filter's parsing.</summary>
public class OwnerAndTypeRulesTests
{
    [Theory]
    [InlineData(UserStatus.Active, UserStatus.Active, true)]
    [InlineData(UserStatus.Active, UserStatus.Inactive, true)]
    [InlineData(UserStatus.Inactive, UserStatus.Active, true)]
    [InlineData(UserStatus.Active, null, true)]
    [InlineData(null, UserStatus.Active, true)]
    [InlineData(UserStatus.Inactive, UserStatus.Inactive, false)]
    [InlineData(UserStatus.Inactive, null, false)]
    [InlineData(UserStatus.PendingSetup, UserStatus.Inactive, false)]
    [InlineData(null, null, false)]
    public void A_dashboard_needs_at_least_one_active_owner(UserStatus? primary, UserStatus? backup, bool expected) =>
        Assert.Equal(expected, OwnerDepartureService.HasActiveOwner(primary, backup));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("all", null)]
    [InlineData("ALL", null)]
    [InlineData("PowerBi", DashboardType.PowerBi)]
    [InlineData("powerbi", DashboardType.PowerBi)]
    [InlineData("Power BI", DashboardType.PowerBi)]
    [InlineData("Tableau", DashboardType.Tableau)]
    [InlineData("GenAi", DashboardType.GenAi)]
    [InlineData(" genai ", DashboardType.GenAi)]
    public void The_analytics_type_filter_reads_a_type_or_means_all(string? text, DashboardType? expected) =>
        Assert.Equal(expected, AnalyticsService.ParseType(text));

    [Theory]
    [InlineData("Sisense")]
    [InlineData("tablaeu")]
    [InlineData("7")]
    [InlineData("PowerBi,Tableau")]
    public void An_unknown_type_is_refused_so_a_typo_cannot_quietly_show_everything(string text) =>
        Assert.Throws<RuleException>(() => AnalyticsService.ParseType(text));
}

/// <summary>Dashboards whose owners have left go Inactive until a Super Admin names new ones; analytics can be limited to one type. Needs VANTAGE_TEST_SQL.</summary>
public class OwnerDepartureTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, OwnerDepartureService Departures, DashboardMasterService Master, DashboardFactory Factory, User SuperAdmin) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var notifications = new NotificationService(db, clock);
        var email = new EmailOutboxService(db, Options.Create(new EmailOptions()), clock);
        var ownership = new OwnershipService(db, clock);
        var files = new Vantage.Infrastructure.Storage.LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));
        var master = new DashboardMasterService(db, ownership, new PowerBiClient(new HttpClient(), new NoTokens(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PowerBiClient>.Instance), files, audit, clock);
        var admin = await PersonAsync(db);
        var role = await db.Roles.SingleAsync(r => r.Name == SystemRoles.SuperAdmin);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id, AssignedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new Kit(db, new OwnerDepartureService(db, audit, notifications, email, clock), master, new DashboardFactory(db, ownership, audit, clock), admin);
    }

    private sealed class NoTokens : IPowerBiTokenProvider
    {
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) => Task.FromResult("t");
    }

    private static async Task<User> PersonAsync(AppDbContext db, UserStatus status = UserStatus.Active)
    {
        var u = new User { Email = Unique("p") + "@rrd.com", DisplayName = Unique("Person "), UserType = UserType.Internal, Status = status, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static Task SetStatusAsync(AppDbContext db, int userId, UserStatus status) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status));

    private async Task<Dashboard> DashboardAsync(Kit k, User primary, User? backup = null, DashboardType type = DashboardType.PowerBi, DashboardStatus status = DashboardStatus.Active)
    {
        var d = await k.Factory.CreateAsync(new Dashboard
        {
            Code = "OD", Name = Unique("Own dash "), Type = type, Status = status, PrimaryOwnerId = primary.Id, BackupOwnerId = backup?.Id,
        }, null, primary.Id);
        k.Db.ChangeTracker.Clear();
        return d;
    }

    private static Task<Dashboard> LoadAsync(AppDbContext db, int id) => db.Dashboards.AsNoTracking().SingleAsync(d => d.Id == id);

    [SqlFact]
    public async Task A_dashboard_whose_owners_have_both_left_goes_inactive_and_the_super_admins_are_told_once()
    {
        await using var k = await ArrangeAsync();
        var primary = await PersonAsync(k.Db);
        var backup = await PersonAsync(k.Db);
        var d = await DashboardAsync(k, primary, backup);
        await SetStatusAsync(k.Db, primary.Id, UserStatus.Inactive);
        await SetStatusAsync(k.Db, backup.Id, UserStatus.Inactive);

        var found = await k.Departures.ReviewAsync();

        Assert.Contains(found, f => f.Id == d.Id);
        var after = await LoadAsync(k.Db, d.Id);
        Assert.Equal(DashboardStatus.Inactive, after.Status);
        Assert.True(after.OwnershipPendingReview);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "dashboard.inactivated-no-owner" && a.DashboardId == d.Id));
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.SuperAdmin.Id && n.Type == "dashboard.no-owner" && n.Link == $"/dashboards/{d.Id}"));
        Assert.True(await k.Db.EmailOutbox.AnyAsync(e => e.ToAddress == k.SuperAdmin.Email && e.Subject.Contains(d.Name)));

        var notices = await k.Db.Notifications.CountAsync(n => n.UserId == k.SuperAdmin.Id && n.Type == "dashboard.no-owner" && n.Link == $"/dashboards/{d.Id}");
        Assert.DoesNotContain(await k.Departures.ReviewAsync(), f => f.Id == d.Id);                // already handled: not touched or announced again
        Assert.Equal(notices, await k.Db.Notifications.CountAsync(n => n.UserId == k.SuperAdmin.Id && n.Type == "dashboard.no-owner" && n.Link == $"/dashboards/{d.Id}"));
    }

    [SqlFact]
    public async Task A_dashboard_keeps_running_while_either_owner_is_still_active_and_only_active_dashboards_are_touched()
    {
        await using var k = await ArrangeAsync();
        var gone = await PersonAsync(k.Db);
        var stays = await PersonAsync(k.Db);
        var backupLeft = await DashboardAsync(k, stays, gone);                                   // the primary stays
        var primaryLeft = await DashboardAsync(k, gone, stays);                                  // the backup stays
        var draft = await DashboardAsync(k, gone, null, status: DashboardStatus.Draft);
        var manual = await DashboardAsync(k, gone, null, status: DashboardStatus.Inactive);
        var retired = await DashboardAsync(k, gone, null, status: DashboardStatus.Retired);
        await SetStatusAsync(k.Db, gone.Id, UserStatus.Inactive);

        await k.Departures.ReviewAsync();

        Assert.Equal(DashboardStatus.Active, (await LoadAsync(k.Db, backupLeft.Id)).Status);
        Assert.Equal(DashboardStatus.Active, (await LoadAsync(k.Db, primaryLeft.Id)).Status);
        Assert.Equal(DashboardStatus.Draft, (await LoadAsync(k.Db, draft.Id)).Status);
        Assert.False((await LoadAsync(k.Db, manual.Id)).OwnershipPendingReview);               // already Inactive for another reason: left alone
        Assert.Equal(DashboardStatus.Retired, (await LoadAsync(k.Db, retired.Id)).Status);
    }

    [SqlFact]
    public async Task Naming_a_new_active_owner_makes_the_dashboard_live_again_and_other_edits_do_not_reactivate_a_manually_inactive_one()
    {
        await using var k = await ArrangeAsync();
        var oldOwner = await PersonAsync(k.Db);
        var newOwner = await PersonAsync(k.Db);
        var d = await DashboardAsync(k, oldOwner);
        await SetStatusAsync(k.Db, oldOwner.Id, UserStatus.Inactive);
        await k.Departures.ReviewAsync();
        Assert.Equal(DashboardStatus.Inactive, (await LoadAsync(k.Db, d.Id)).Status);

        // Naming another inactive person is refused, so it stays Inactive.
        var stillGone = await PersonAsync(k.Db, UserStatus.Inactive);
        await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.Id, Details(d, stillGone.Id), k.SuperAdmin.Id));
        Assert.Equal(DashboardStatus.Inactive, (await LoadAsync(k.Db, d.Id)).Status);

        await k.Master.UpdateAsync(d.Id, Details(d, newOwner.Id), k.SuperAdmin.Id);

        var after = await LoadAsync(k.Db, d.Id);
        Assert.Equal(DashboardStatus.Active, after.Status);
        Assert.False(after.OwnershipPendingReview);
        Assert.Equal(newOwner.Id, after.PrimaryOwnerId);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "dashboard.reactivated-new-owner" && a.DashboardId == d.Id));
        Assert.True(await k.Db.GroupMembers.AnyAsync(m => m.DashboardId == d.Id && m.UserId == newOwner.Id && m.RemovedAtUtc == null && m.Group.IsDefault));

        // A dashboard a Super Admin made Inactive by hand is not woken by an ordinary edit.
        var manual = await DashboardAsync(k, newOwner, status: DashboardStatus.Inactive);
        await k.Master.UpdateAsync(manual.Id, Details(manual, newOwner.Id), k.SuperAdmin.Id);
        Assert.Equal(DashboardStatus.Inactive, (await LoadAsync(k.Db, manual.Id)).Status);
    }

    private static DashboardDetailsInput Details(Dashboard d, int primaryOwnerId) =>
        new(d.Name, null, null, primaryOwnerId, null, null, d.Audience, d.DataClassification, null, null);

    // ------------------------------------------------------------ Analytics type filter

    [SqlFact]
    public async Task The_analytics_scope_narrows_to_one_type_and_an_owner_only_sees_their_own_of_that_type()
    {
        await using var k = await ArrangeAsync();
        var owner = await PersonAsync(k.Db);
        var other = await PersonAsync(k.Db);
        var pbi = await DashboardAsync(k, owner, type: DashboardType.PowerBi);
        var tab = await DashboardAsync(k, owner, type: DashboardType.Tableau);
        var gen = await DashboardAsync(k, owner, type: DashboardType.GenAi);
        var othersTab = await DashboardAsync(k, other, type: DashboardType.Tableau);
        var analytics = new AnalyticsService(k.Db, TimeProvider.System);

        Assert.Null(await analytics.ScopeAsync(null, null));                                      // all types, everyone: no filter
        Assert.Null(await analytics.ScopeAsync("all", null));
        var tableau = await analytics.ScopeAsync("Tableau", null);
        Assert.Contains(tab.Id, tableau!); Assert.Contains(othersTab.Id, tableau!);
        Assert.DoesNotContain(pbi.Id, tableau!); Assert.DoesNotContain(gen.Id, tableau!);

        var owned = await analytics.OwnedAsync(owner.Id);
        Assert.Equal(owned.Order(), (await analytics.ScopeAsync(null, owned))!.Order());          // owner, all types: just what they own
        Assert.Equal([tab.Id], await analytics.ScopeAsync("Tableau", owned));                     // owner, one type: theirs of that type only
        Assert.Empty((await analytics.ScopeAsync("GenAi", await analytics.OwnedAsync(other.Id)))!);   // nothing matches: empty, not "everything"
    }

    [SqlFact]
    public async Task Views_are_counted_only_for_the_chosen_type()
    {
        await using var k = await ArrangeAsync();
        var owner = await PersonAsync(k.Db);
        var viewer = await PersonAsync(k.Db);
        var pbi = await DashboardAsync(k, owner, type: DashboardType.PowerBi);
        var tab = await DashboardAsync(k, owner, type: DashboardType.Tableau);
        var now = DateTime.UtcNow;
        k.Db.DashboardViews.AddRange(
            new DashboardView { DashboardId = pbi.Id, UserId = viewer.Id, Source = ViewSource.Portal, ViewedAtUtc = now },
            new DashboardView { DashboardId = pbi.Id, UserId = viewer.Id, Source = ViewSource.Portal, ViewedAtUtc = now },
            new DashboardView { DashboardId = tab.Id, UserId = viewer.Id, Source = ViewSource.Portal, ViewedAtUtc = now });
        await k.Db.SaveChangesAsync();
        var analytics = new AnalyticsService(k.Db, TimeProvider.System);
        var mine = await analytics.OwnedAsync(owner.Id);

        var all = (await analytics.DashboardsAsync(30, await analytics.ScopeAsync(null, mine))).ToDictionary(r => r.Id);
        var onlyTableau = (await analytics.DashboardsAsync(30, await analytics.ScopeAsync("Tableau", mine)));

        Assert.Equal(2, all[pbi.Id].Views);
        Assert.Equal(1, all[tab.Id].Views);
        Assert.Equal(tab.Id, Assert.Single(onlyTableau).Id);
        Assert.Equal("Tableau", onlyTableau[0].Type);
        var overview = await analytics.OverviewAsync(30, await analytics.ScopeAsync("PowerBi", mine));
        Assert.Equal(2, overview.Views);
    }
}
