using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Access Groups: adding, moving, removing and copying members; status and rename rules.</summary>
public class AccessGroupTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, AccessGroupService Access, GroupService Groups, GroupActor Admin, User Owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var groups = new GroupService(db, audit, clock);
        var access = new AccessGroupService(db, groups, new NotificationService(db, clock), audit, clock);
        var owner = await UserAsync(db);
        return new Kit(db, access, groups, new GroupActor(owner.Id, IsSuperAdmin: true), owner);
    }

    private static async Task<User> UserAsync(AppDbContext db, UserStatus status = UserStatus.Active)
    {
        var u = new User { Email = Unique("u") + "@rrd.com", DisplayName = Unique("User "), UserType = UserType.Internal, Status = status, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<(Dashboard Dashboard, DashboardGroup Default)> DashboardAsync(Kit k, bool rls)
    {
        var factory = new DashboardFactory(k.Db, new OwnershipService(k.Db, TimeProvider.System), new AuditWriter(k.Db, new Ctx(), TimeProvider.System), TimeProvider.System);
        var d = await factory.CreateAsync(new Dashboard
        {
            Code = "AG", Name = Unique("Dash "), Type = DashboardType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = rls, PrimaryOwnerId = k.Owner.Id,
        }, rls ? "Region_All" : null, k.Owner.Id);
        var g = await k.Db.DashboardGroups.SingleAsync(x => x.DashboardId == d.Id && x.IsDefault);
        return (d, g);
    }

    private static Task<int?> LiveGroupAsync(AppDbContext db, int dashboardId, int userId) =>
        db.GroupMembers.Where(m => m.DashboardId == dashboardId && m.UserId == userId && m.RemovedAtUtc == null).Select(m => (int?)m.GroupId).SingleOrDefaultAsync();

    [SqlFact]
    public async Task Adding_by_email_adds_portal_users_only_and_reports_each_line()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: false);
        var known = await UserAsync(k.Db);
        var leaver = await UserAsync(k.Db, UserStatus.Inactive);
        var external = Unique("ext") + "@client.com";

        var results = await k.Access.AddMembersAsync(def.Id, [known.Email, known.Email.ToUpperInvariant(), external, leaver.Email, "not-an-email", k.Owner.Email],
            false, MembershipSource.BulkUpload, k.Admin, serviceNowReference: "RITM0012345");

        Assert.Equal("Added", results.Single(r => r.Email == known.Email).Outcome);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.EntityId == def.Id.ToString() && a.Action == "group.members-added" && a.ServiceNowReference == "RITM0012345"));
        Assert.Equal("NotAUser", results.Single(r => r.Email == external).Outcome);
        Assert.Equal("Inactive", results.Single(r => r.Email == leaver.Email).Outcome);
        Assert.Equal("Invalid", results.Single(r => r.Email == "not-an-email").Outcome);
        Assert.Equal("AlreadyHere", results.Single(r => r.Email == k.Owner.Email).Outcome);
        Assert.Equal(def.Id, await LiveGroupAsync(k.Db, d.Id, known.Id));
        Assert.False(await k.Db.Users.AnyAsync(u => u.Email == external)); // users are added in Users first, never here
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == known.Id && n.Type == "access.granted"));
    }

    [SqlFact]
    public async Task People_in_another_group_are_skipped_unless_moving_is_asked_for()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: true);
        var north = await k.Groups.AddAsync(d.Id, Unique("N"), "Region_North", k.Owner.Id);
        var user = await UserAsync(k.Db);
        await k.Access.AddMembersAsync(def.Id, [user.Email], false, MembershipSource.Manual, k.Admin);

        var skipped = await k.Access.AddMembersAsync(north.Id, [user.Email], false, MembershipSource.Manual, k.Admin);
        Assert.Equal("InOtherGroup", skipped.Single().Outcome);
        Assert.Equal(def.Id, await LiveGroupAsync(k.Db, d.Id, user.Id));

        var moved = await k.Access.AddMembersAsync(north.Id, [user.Email], true, MembershipSource.Manual, k.Admin);
        Assert.Equal("Moved", moved.Single().Outcome);
        Assert.Equal(north.Id, await LiveGroupAsync(k.Db, d.Id, user.Id));
    }

    [SqlFact]
    public async Task Moving_keeps_one_live_group_and_stays_within_the_dashboard()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: true);
        var (other, otherDefault) = await DashboardAsync(k, rls: true);
        var south = await k.Groups.AddAsync(d.Id, Unique("S"), "Region_South", k.Owner.Id);
        var user = await UserAsync(k.Db);
        await k.Access.AddMembersAsync(def.Id, [user.Email], false, MembershipSource.Manual, k.Admin);

        await k.Access.MoveMemberAsync(def.Id, user.Id, south.Id, k.Admin);
        Assert.Equal(south.Id, await LiveGroupAsync(k.Db, d.Id, user.Id));
        Assert.Equal(1, await k.Db.GroupMembers.CountAsync(m => m.DashboardId == d.Id && m.UserId == user.Id && m.RemovedAtUtc != null));

        await Assert.ThrowsAsync<RuleException>(() => k.Access.MoveMemberAsync(south.Id, user.Id, otherDefault.Id, k.Admin));
    }

    [SqlFact]
    public async Task Owners_cannot_be_removed_but_others_can_and_their_history_stays()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: false);
        var user = await UserAsync(k.Db);
        await k.Access.AddMembersAsync(def.Id, [user.Email], false, MembershipSource.Manual, k.Admin);

        Assert.Contains("owns this dashboard", (await Assert.ThrowsAsync<RuleException>(() => k.Access.RemoveMemberAsync(def.Id, k.Owner.Id, k.Admin))).Message);
        await k.Access.RemoveMemberAsync(def.Id, user.Id, k.Admin);

        Assert.Null(await LiveGroupAsync(k.Db, d.Id, user.Id));
        Assert.True(await k.Db.GroupMembers.AnyAsync(m => m.GroupId == def.Id && m.UserId == user.Id && m.RemovedReason == "Removed"));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.EntityType == "DashboardGroup" && a.EntityId == def.Id.ToString() && a.Action == "group.member-removed"));
    }

    [SqlFact]
    public async Task The_default_group_stays_active_and_keeps_its_name()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: true);
        var extra = await k.Groups.AddAsync(d.Id, Unique("X"), "Region_East", k.Owner.Id);

        await Assert.ThrowsAsync<RuleException>(() => k.Access.SetActiveAsync(def.Id, false, k.Admin));
        await Assert.ThrowsAsync<RuleException>(() => k.Access.RenameAsync(def.Id, "NewName", k.Admin));

        await k.Access.SetActiveAsync(extra.Id, false, k.Admin);
        var user = await UserAsync(k.Db);
        await Assert.ThrowsAsync<RuleException>(() => k.Access.AddMembersAsync(extra.Id, [user.Email], false, MembershipSource.Manual, k.Admin));
        var newName = Unique("Renamed");
        await k.Access.RenameAsync(extra.Id, newName, k.Admin);
        Assert.Equal(newName, (await k.Db.DashboardGroups.AsNoTracking().SingleAsync(g => g.Id == extra.Id)).Name);
    }

    [SqlFact]
    public async Task Cloning_copies_members_only_and_skips_people_already_on_the_target_dashboard()
    {
        await using var k = await ArrangeAsync();
        var (source, sourceDefault) = await DashboardAsync(k, rls: false);
        var a = await UserAsync(k.Db);
        var b = await UserAsync(k.Db);
        await k.Access.AddMembersAsync(sourceDefault.Id, [a.Email, b.Email], false, MembershipSource.Manual, k.Admin);
        var (target, targetDefault) = await DashboardAsync(k, rls: true);
        await k.Access.AddMembersAsync(targetDefault.Id, [b.Email], false, MembershipSource.Manual, k.Admin);

        var (clone, copied) = await k.Access.CreateAsync(target.Id, Unique("Clone"), "Region_West", sourceDefault.Id, k.Admin);

        Assert.Equal("Region_West", clone.RlsValue);
        Assert.Equal(sourceDefault.Id, clone.ClonedFromGroupId);
        Assert.Equal("Added", copied.Single(r => r.Email == a.Email).Outcome);
        Assert.Equal("InOtherGroup", copied.Single(r => r.Email == b.Email).Outcome);
        // The source owner is the target owner too, so they stay in the target's default group.
        Assert.Equal(targetDefault.Id, await LiveGroupAsync(k.Db, target.Id, k.Owner.Id));
        Assert.Equal(MembershipSource.Clone, await k.Db.GroupMembers.Where(m => m.GroupId == clone.Id && m.UserId == a.Id).Select(m => m.Source).SingleAsync());
    }

    [SqlFact]
    public async Task Access_changes_are_paused_for_non_super_admins_while_owners_are_under_review()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: false);
        await k.Db.Dashboards.Where(x => x.Id == d.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OwnershipPendingReview, true));
        k.Db.ChangeTracker.Clear();
        var user = await UserAsync(k.Db);

        await Assert.ThrowsAsync<RuleException>(() => k.Access.AddMembersAsync(def.Id, [user.Email], false, MembershipSource.Manual, new GroupActor(k.Owner.Id, false)));
        var ok = await k.Access.AddMembersAsync(def.Id, [user.Email], false, MembershipSource.Manual, k.Admin);
        Assert.Equal("Added", ok.Single().Outcome);
    }

    [SqlFact]
    public async Task Details_save_name_rls_value_and_status_together()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: true);
        var g = await k.Groups.AddAsync(d.Id, Unique("G"), "Region_North", k.Owner.Id);
        var newName = Unique("Edited");

        await k.Access.UpdateDetailsAsync(g.Id, newName, "Region_North, Region_East", active: false, k.Admin);

        k.Db.ChangeTracker.Clear();
        var saved = await k.Db.DashboardGroups.SingleAsync(x => x.Id == g.Id);
        Assert.Equal((newName, "Region_North,Region_East", GroupStatus.Inactive), (saved.Name, saved.RlsValue, saved.Status));
        // The default group keeps its name and stays active; its RLS value can still change.
        await Assert.ThrowsAsync<RuleException>(() => k.Access.UpdateDetailsAsync(def.Id, def.Name, "Region_All", active: false, k.Admin));
        await k.Access.UpdateDetailsAsync(def.Id, def.Name, "Region_West", active: true, k.Admin);
        Assert.Equal("Region_West", (await k.Db.DashboardGroups.AsNoTracking().SingleAsync(x => x.Id == def.Id)).RlsValue);
    }
}
