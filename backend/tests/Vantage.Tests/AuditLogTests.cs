using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The Audit Log viewer: filters, the 12-month search window, paging and the Details lookup.</summary>
public class AuditLogTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static AuditLog Row(string tag, DateTime at, int? actor = null, string action = "user.updated", string entity = "User", string? sn = null, string? details = null) => new()
    {
        OccurredAtUtc = at, ActorUserId = actor, Action = action + "." + tag, EntityType = entity + tag, EntityId = tag, ServiceNowReference = sn, Details = details,
    };

    private static AuditQuery Q(string tag, DateOnly? from = null, DateOnly? to = null, string? actor = null, string? sn = null, string? search = null, int page = 1, int size = 50)
        => new(from, to, actor, "User" + tag, null, sn, search, null, page, size);

    private static async Task<User> UserAsync(AppDbContext db, string name)
    {
        var u = new User { Email = Unique("a") + "@rrd.com", DisplayName = name, UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [SqlFact]
    public async Task Filters_narrow_by_date_actor_ticket_and_text()
    {
        await using var db = fx.CreateContext();
        var tag = Unique("T");
        var now = DateTime.UtcNow;
        var nimal = await UserAsync(db, Unique("Nimal "));
        var priya = await UserAsync(db, Unique("Priya "));
        db.AuditLogs.AddRange(
            Row(tag, now.AddDays(-1), nimal.Id, sn: "INC0001", details: """{"rolesBefore":["A"],"rolesAfter":["B"]}"""),
            Row(tag, now.AddDays(-10), priya.Id, sn: "CHG0042"),
            Row(tag, now.AddDays(-20), null));
        await db.SaveChangesAsync();
        var svc = new AuditLogService(db, TimeProvider.System);

        Assert.Equal(3, (await svc.SearchAsync(Q(tag), default)).Total);
        Assert.Equal(2, (await svc.SearchAsync(Q(tag, from: DateOnly.FromDateTime(now.AddDays(-12))), default)).Total);
        Assert.Equal(2, (await svc.SearchAsync(Q(tag, to: DateOnly.FromDateTime(now.AddDays(-10))), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, actor: nimal.DisplayName![..8]), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, actor: priya.Email), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, sn: "CHG004"), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, search: "rolesAfter"), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, search: "CHG004"), default)).Total);          // the ordinary search finds a ticket number too

        var first = (await svc.SearchAsync(Q(tag, actor: nimal.Email), default)).Rows.Single();
        Assert.Equal(nimal.DisplayName, first.Actor);
        Assert.Null(first.Details);                                   // the grid never carries Details
        Assert.Contains("rolesBefore", (await svc.GetAsync(first.Id, default)).Details);
    }

    [SqlFact]
    public async Task Search_stops_at_twelve_months_but_rows_stay_in_the_database()
    {
        await using var db = fx.CreateContext();
        var tag = Unique("O");
        var now = DateTime.UtcNow;
        db.AuditLogs.AddRange(Row(tag, now.AddDays(-5)), Row(tag, now.AddMonths(-13)));
        await db.SaveChangesAsync();
        var svc = new AuditLogService(db, TimeProvider.System);

        Assert.Equal(1, (await svc.SearchAsync(Q(tag), default)).Total);
        Assert.Equal(1, (await svc.SearchAsync(Q(tag, from: new DateOnly(2000, 1, 1)), default)).Total);   // asking for more doesn't reach back further
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityType == "User" + tag));
        Assert.Single((await svc.ExportAsync(Q(tag), default)).Rows);
    }

    [SqlFact]
    public async Task Results_are_newest_first_and_paged()
    {
        await using var db = fx.CreateContext();
        var tag = Unique("P");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++) db.AuditLogs.Add(Row(tag, now.AddHours(-i)));
        await db.SaveChangesAsync();
        var svc = new AuditLogService(db, TimeProvider.System);

        var p1 = await svc.SearchAsync(Q(tag, size: 2), default);
        var p3 = await svc.SearchAsync(Q(tag, page: 3, size: 2), default);
        Assert.Equal(5, p1.Total);
        Assert.Equal(2, p1.Rows.Count);
        Assert.Single(p3.Rows);
        Assert.True(p1.Rows[0].OccurredAtUtc > p1.Rows[1].OccurredAtUtc);
        Assert.Equal(now.AddHours(-4), p3.Rows[0].OccurredAtUtc, TimeSpan.FromSeconds(1));
    }

    [SqlFact]
    public async Task Missing_entry_is_not_found()
    {
        await using var db = fx.CreateContext();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new AuditLogService(db, TimeProvider.System).GetAsync(-1, default));
    }
}
