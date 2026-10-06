using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Personal folders: naming rules, who can add what, privacy between users, and revoked access.</summary>
public class PersonalFolderTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static async Task<User> UserAsync(AppDbContext db)
    {
        var u = new User { Email = Unique("f") + "@rrd.com", DisplayName = Unique("User "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>A dashboard whose primary owner (and so a member of its default group) is the given user.</summary>
    private static async Task<Dashboard> DashboardOwnedByAsync(AppDbContext db, User owner)
    {
        var factory = new DashboardFactory(db, new OwnershipService(db, TimeProvider.System), new AuditWriter(db, new Ctx(), TimeProvider.System), TimeProvider.System);
        return await factory.CreateAsync(new Dashboard
        {
            Code = "PF", Name = Unique("Dash "), Type = BiType.PowerBi, Status = DashboardStatus.Active, PrimaryOwnerId = owner.Id,
        }, null, owner.Id);
    }

    [SqlFact]
    public async Task Folder_names_are_trimmed_required_and_unique_per_user()
    {
        await using var db = fx.CreateContext();
        var svc = new PersonalFolderService(db, TimeProvider.System);
        var me = await UserAsync(db);
        var other = await UserAsync(db);
        var name = Unique("Reports ");

        var folder = await svc.CreateAsync(me.Id, "  " + name + "  ");
        Assert.Equal(name, folder.Name);
        await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(me.Id, name.ToUpperInvariant()));
        await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(me.Id, "   "));
        await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(me.Id, new string('x', Rules.FolderNameMax + 1)));
        await svc.CreateAsync(other.Id, name);                                   // the same name is fine for someone else

        var second = await svc.CreateAsync(me.Id, Unique("Other "));
        await Assert.ThrowsAsync<RuleException>(() => svc.RenameAsync(me.Id, second.Id, name));
        await svc.RenameAsync(me.Id, second.Id, name + " 2");
        Assert.Contains(await svc.ListAsync(me.Id), f => f.Name == name + " 2");
    }

    [SqlFact]
    public async Task A_user_can_have_only_so_many_folders()
    {
        await using var db = fx.CreateContext();
        var svc = new PersonalFolderService(db, TimeProvider.System);
        var me = await UserAsync(db);
        for (var i = 0; i < Rules.FoldersPerUserMax; i++) await svc.CreateAsync(me.Id, $"Folder {i}");
        var ex = await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(me.Id, "One too many"));
        Assert.Contains(Rules.FoldersPerUserMax.ToString(), ex.Message);
    }

    [SqlFact]
    public async Task Only_dashboards_the_user_can_open_go_in_and_revoked_ones_disappear()
    {
        await using var db = fx.CreateContext();
        var svc = new PersonalFolderService(db, TimeProvider.System);
        var me = await UserAsync(db);
        var stranger = await UserAsync(db);
        var mine = await DashboardOwnedByAsync(db, me);
        var notMine = await DashboardOwnedByAsync(db, stranger);
        var folder = await svc.CreateAsync(me.Id, Unique("F "));

        await svc.AddDashboardAsync(me.Id, folder.Id, mine.Id);
        await svc.AddDashboardAsync(me.Id, folder.Id, mine.Id);                  // adding twice changes nothing
        await Assert.ThrowsAsync<RuleException>(() => svc.AddDashboardAsync(me.Id, folder.Id, notMine.Id));
        Assert.Equal([mine.Id], (await svc.ListAsync(me.Id)).Single(f => f.Id == folder.Id).DashboardIds);

        var membership = await db.GroupMembers.SingleAsync(m => m.UserId == me.Id && m.DashboardId == mine.Id && m.RemovedAtUtc == null);
        membership.RemovedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Empty((await svc.ListAsync(me.Id)).Single(f => f.Id == folder.Id).DashboardIds);

        membership.RemovedAtUtc = null;                                          // access granted again: it is back
        await db.SaveChangesAsync();
        Assert.Equal([mine.Id], (await svc.ListAsync(me.Id)).Single(f => f.Id == folder.Id).DashboardIds);

        await svc.RemoveDashboardAsync(me.Id, folder.Id, mine.Id);
        Assert.Empty((await svc.ListAsync(me.Id)).Single(f => f.Id == folder.Id).DashboardIds);
    }

    [SqlFact]
    public async Task Folders_are_private_and_deleting_one_removes_its_items()
    {
        await using var db = fx.CreateContext();
        var svc = new PersonalFolderService(db, TimeProvider.System);
        var me = await UserAsync(db);
        var other = await UserAsync(db);
        var dash = await DashboardOwnedByAsync(db, me);
        var folder = await svc.CreateAsync(me.Id, Unique("Mine "));
        await svc.AddDashboardAsync(me.Id, folder.Id, dash.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RenameAsync(other.Id, folder.Id, "Stolen"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteAsync(other.Id, folder.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.AddDashboardAsync(other.Id, folder.Id, dash.Id));
        Assert.Empty(await svc.ListAsync(other.Id));

        await svc.DeleteAsync(me.Id, folder.Id);
        Assert.Empty(await svc.ListAsync(me.Id));
        Assert.Equal(0, await db.PersonalFolderItems.CountAsync(i => i.FolderId == folder.Id));
    }
}
