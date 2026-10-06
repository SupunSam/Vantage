using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>C59: who counts as an owner for managing a dashboard from My Dashboards.</summary>
public class DashboardOwnerTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    [SqlFact]
    public async Task Owners_manage_their_own_active_dashboards_and_nobody_elses()
    {
        await using var db = fx.CreateContext();
        User[] people = [new() { Email = Unique("a") + "@rrd.com", DisplayName = "A", UserType = UserType.Internal, Status = UserStatus.Active, CreatedAtUtc = DateTime.UtcNow },
                         new() { Email = Unique("b") + "@rrd.com", DisplayName = "B", UserType = UserType.Internal, Status = UserStatus.Active, CreatedAtUtc = DateTime.UtcNow },
                         new() { Email = Unique("c") + "@rrd.com", DisplayName = "C", UserType = UserType.Internal, Status = UserStatus.Active, CreatedAtUtc = DateTime.UtcNow }];
        db.Users.AddRange(people);
        await db.SaveChangesAsync();
        var (primary, backup, stranger) = (people[0], people[1], people[2]);

        var live = new Dashboard { Code = "OWN001", Name = Unique("Live "), Type = BiType.PowerBi, Status = DashboardStatus.Active, PrimaryOwnerId = primary.Id, BackupOwnerId = backup.Id };
        var retired = new Dashboard { Code = "OWN002", Name = Unique("Retired "), Type = BiType.PowerBi, Status = DashboardStatus.Retired, PrimaryOwnerId = stranger.Id };
        var paused = new Dashboard { Code = "OWN003", Name = Unique("Paused "), Type = BiType.PowerBi, Status = DashboardStatus.Inactive, PrimaryOwnerId = stranger.Id, OwnershipPendingReview = true };
        db.Dashboards.AddRange(live, retired, paused);
        await db.SaveChangesAsync();

        Assert.True(await DashboardOwners.IsAsync(db, primary.Id, live.Id));
        Assert.True(await DashboardOwners.IsAsync(db, backup.Id, live.Id));
        Assert.False(await DashboardOwners.IsAsync(db, stranger.Id, live.Id));
        Assert.False(await DashboardOwners.IsAsync(db, stranger.Id, retired.Id));      // retired dashboards are closed
        Assert.False(await DashboardOwners.IsAsync(db, stranger.Id, paused.Id));       // only Super Admins act while ownership is under review (C43)
        Assert.True(await DashboardOwners.OwnsAnyAsync(db, backup.Id));
        Assert.False(await DashboardOwners.OwnsAnyAsync(db, stranger.Id));
    }
}
