using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Adding people to an access group goes to the owners as one request they approve in part or in full (C26).</summary>
public class GroupAddRequestTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, GroupAddRequestService Requests, AccessGroupService Access, GroupService Groups,
        User Owner, User Admin, User SuperAdmin, Dashboard Dashboard, DashboardGroup Default) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
        public GroupActor OwnerActor => new(Owner.Id, false);
        public GroupActor AdminActor => new(Admin.Id, false);
        public GroupActor SuperActor => new(SuperAdmin.Id, true);
    }

    private async Task<Kit> ArrangeAsync(bool rls = true)
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var groups = new GroupService(db, audit, clock);
        var notifications = new NotificationService(db, clock);
        var access = new AccessGroupService(db, groups, notifications, audit, clock);
        var email = new EmailOutboxService(db, Options.Create(new EmailOptions()), clock);
        var owner = await UserAsync(db);
        var admin = await UserAsync(db);
        var super = await UserAsync(db);
        var factory = new DashboardFactory(db, new OwnershipService(db, clock), audit, clock);
        var d = await factory.CreateAsync(new Dashboard
        {
            Code = "GA", Name = Unique("Dash "), Type = DashboardType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = rls, PrimaryOwnerId = owner.Id,
        }, rls ? "Region_All" : null, owner.Id);
        var def = await db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault);
        return new Kit(db, new GroupAddRequestService(db, access, notifications, email, audit, clock), access, groups, owner, admin, super, d, def);
    }

    private static async Task<User> UserAsync(AppDbContext db)
    {
        var u = new User { Email = Unique("u") + "@rrd.com", DisplayName = Unique("User "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<List<User>> MakeAsync(AppDbContext db, int n)
    {
        var list = new List<User>();
        for (var i = 0; i < n; i++) list.Add(await UserAsync(db));
        return list;
    }

    private static Task<bool> IsMemberAsync(AppDbContext db, int groupId, int userId) =>
        db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.RemovedAtUtc == null);

    private static IEnumerable<string> Emails(IEnumerable<User> users) => users.Select(u => u.Email);

    [SqlFact]
    public async Task An_admin_adding_people_sends_one_request_and_adds_nobody()
    {
        await using var k = await ArrangeAsync();
        var people = await MakeAsync(k.Db, 5);

        var outcome = await k.Requests.SubmitAsync(k.Default.Id, [.. Emails(people), Unique("ghost") + "@rrd.com"], false, MembershipSource.BulkUpload, k.AdminActor, "INC0042", "Q4 team");

        Assert.Equal("Requested", outcome.Mode);
        Assert.Equal(5, outcome.Results.Count(r => r.Outcome == "Requested"));
        Assert.Single(outcome.Results, r => r.Outcome == "NotAUser");
        var request = await k.Db.GroupAddRequests.Include(r => r.Items).SingleAsync(r => r.Id == outcome.RequestId);
        Assert.Equal(5, request.Items.Count);
        Assert.Equal(GroupAddRequestStatus.Pending, request.Status);
        Assert.Equal("INC0042", request.ServiceNowReference);
        foreach (var p in people) Assert.False(await IsMemberAsync(k.Db, k.Default.Id, p.Id));
        Assert.Equal(1, await k.Db.Notifications.CountAsync(n => n.UserId == k.Owner.Id && n.Type == "group-add.requested" && n.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-5) && n.Title.Contains("5 people")));
        Assert.Equal(1, await k.Db.AuditLogs.CountAsync(a => a.Action == "group.add-requested" && a.EntityId == k.Default.Id.ToString()));
    }

    [SqlFact]
    public async Task The_owner_approves_only_the_people_they_tick_and_the_rest_are_rejected()
    {
        await using var k = await ArrangeAsync();
        var people = await MakeAsync(k.Db, 5);
        var request = (await k.Requests.SubmitAsync(k.Default.Id, Emails(people), false, MembershipSource.BulkUpload, k.AdminActor, null, null)).RequestId!.Value;

        var decided = await k.Requests.DecideAsync(request, [people[0].Id, people[1].Id, people[2].Id], "Not the last two", null, k.OwnerActor);

        Assert.Equal(GroupAddRequestStatus.PartlyApproved, decided.Status);
        Assert.Null(decided.OverrideReason);
        foreach (var p in people.Take(3)) Assert.True(await IsMemberAsync(k.Db, k.Default.Id, p.Id));
        foreach (var p in people.Skip(3)) Assert.False(await IsMemberAsync(k.Db, k.Default.Id, p.Id));
        var items = await k.Db.GroupAddRequestItems.Where(i => i.RequestId == request).ToListAsync();
        Assert.Equal(3, items.Count(i => i.Decision == GroupAddItemDecision.Approved));
        Assert.Equal(2, items.Count(i => i.Decision == GroupAddItemDecision.Rejected));
        var added = await k.Db.GroupMembers.Where(m => m.GroupId == k.Default.Id && m.UserId == people[0].Id).SingleAsync();
        Assert.Equal(k.Admin.Id, added.AddedByUserId);                           // the memberships say the admin added them
        Assert.Equal(MembershipSource.BulkUpload, added.Source);
        Assert.Equal(1, await k.Db.AuditLogs.CountAsync(a => a.Action == "group.add-approved" && a.EntityId == k.Default.Id.ToString()));
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Admin.Id && n.Type == "group-add.decided" && n.Title.Contains("partly approved (3 of 5)")));
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(request, [people[3].Id], null, null, k.OwnerActor));   // already decided
    }

    [SqlFact]
    public async Task Approving_everyone_or_nobody_sets_the_matching_status()
    {
        await using var k = await ArrangeAsync();
        var group = await k.Db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = k.Dashboard.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        await k.Db.SaveChangesAsync();
        var people = await MakeAsync(k.Db, 4);

        var all = (await k.Requests.SubmitAsync(group.Entity.Id, Emails(people.Take(2)), false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;
        Assert.Equal(GroupAddRequestStatus.Approved, (await k.Requests.DecideAsync(all, people.Take(2).Select(p => p.Id).ToList(), null, null, k.OwnerActor)).Status);

        var none = (await k.Requests.SubmitAsync(group.Entity.Id, Emails(people.Skip(2)), false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;
        Assert.Equal(GroupAddRequestStatus.Rejected, (await k.Requests.DecideAsync(none, [], "No", null, k.OwnerActor)).Status);
        foreach (var p in people.Skip(2)) Assert.False(await IsMemberAsync(k.Db, group.Entity.Id, p.Id));
        Assert.Equal(1, await k.Db.AuditLogs.CountAsync(a => a.Action == "group.add-rejected" && a.EntityId == group.Entity.Id.ToString()));
    }

    [SqlFact]
    public async Task Only_the_owners_decide_and_never_their_own_request()
    {
        await using var k = await ArrangeAsync();
        var outsider = await UserAsync(k.Db);
        var person = await UserAsync(k.Db);
        var byAdmin = (await k.Requests.SubmitAsync(k.Default.Id, [person.Email], false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;

        await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(byAdmin, [person.Id], null, null, k.AdminActor));                 // the admin can't approve it
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(byAdmin, [person.Id], null, null, new GroupActor(outsider.Id, false)));
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, person.Id));

        // An owner who is also an admin asking for someone can't approve their own ask either.
        var ownerAsks = (await k.Requests.SubmitAsync(k.Default.Id, [(await UserAsync(k.Db)).Email], false, MembershipSource.Manual, k.OwnerActor, null, null)).RequestId!.Value;
        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(ownerAsks, [], null, null, k.OwnerActor));
        Assert.Contains("yourself", ex.Message);
    }

    [SqlFact]
    public async Task A_super_admin_can_decide_for_the_owners_only_with_a_reason_that_is_recorded()
    {
        await using var k = await ArrangeAsync();
        var person = await UserAsync(k.Db);
        var request = (await k.Requests.SubmitAsync(k.Default.Id, [person.Email], false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;

        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(request, [person.Id], null, "  ", k.SuperActor));
        Assert.Contains("reason", ex.Message);
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, person.Id));

        var decided = await k.Requests.DecideAsync(request, [person.Id], null, "Owner is on leave; month-end deadline", k.SuperActor);
        Assert.Equal("Owner is on leave; month-end deadline", decided.OverrideReason);
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, person.Id));
        var audit = await k.Db.AuditLogs.Where(a => a.EntityId == k.Default.Id.ToString() && (a.Action == "group.add-approved" || a.Action == "group.members-added")).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Contains("Owner is on leave", a.Details));      // in the audit log and so in the group's history
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "group-add.overridden" && n.Body!.Contains("Owner is on leave")));   // the owners are told
    }

    [SqlFact]
    public async Task A_super_admin_adding_themselves_also_goes_for_approval()
    {
        await using var k = await ArrangeAsync();
        var outcome = await k.Requests.SubmitAsync(k.Default.Id, [k.SuperAdmin.Email], false, MembershipSource.Manual, k.SuperActor, null, null);
        Assert.Equal("Requested", outcome.Mode);
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, k.SuperAdmin.Id));
        await k.Requests.DecideAsync(outcome.RequestId!.Value, [k.SuperAdmin.Id], null, null, k.OwnerActor);
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, k.SuperAdmin.Id));
    }

    [SqlFact]
    public async Task Someone_already_waiting_is_not_requested_twice_and_people_in_another_group_move_only_on_approval()
    {
        await using var k = await ArrangeAsync();
        var north = (await k.Db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = k.Dashboard.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow })).Entity;
        await k.Db.SaveChangesAsync();
        var person = await UserAsync(k.Db);
        await k.Access.AddMembersAsync(k.Default.Id, [person.Email], false, MembershipSource.Manual, k.SuperActor);

        var first = await k.Requests.SubmitAsync(north.Id, [person.Email], moveFromOtherGroups: true, MembershipSource.Manual, k.AdminActor, null, null);
        Assert.Equal("Requested", first.Mode);
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, person.Id));           // still where they were
        Assert.False(await IsMemberAsync(k.Db, north.Id, person.Id));

        var again = await k.Requests.SubmitAsync(north.Id, [person.Email], true, MembershipSource.Manual, k.AdminActor, null, null);
        Assert.Equal("Nothing", again.Mode);
        Assert.Equal("AlreadyWaiting", again.Results.Single().Outcome);

        await k.Requests.DecideAsync(first.RequestId!.Value, [person.Id], null, null, k.OwnerActor);
        Assert.True(await IsMemberAsync(k.Db, north.Id, person.Id));
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, person.Id));
    }

    [SqlFact]
    public async Task A_request_can_be_withdrawn_and_cannot_be_approved_into_an_inactive_group()
    {
        await using var k = await ArrangeAsync();
        var north = (await k.Db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = k.Dashboard.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow })).Entity;
        await k.Db.SaveChangesAsync();
        var person = await UserAsync(k.Db);
        var request = (await k.Requests.SubmitAsync(north.Id, [person.Email], false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;

        north.Status = GroupStatus.Inactive;
        await k.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.DecideAsync(request, [person.Id], null, null, k.OwnerActor));
        Assert.Equal(GroupAddRequestStatus.Pending, (await k.Db.GroupAddRequests.AsNoTracking().SingleAsync(r => r.Id == request)).Status);

        var stranger = await UserAsync(k.Db);
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.CancelAsync(request, new GroupActor(stranger.Id, false)));   // not theirs
        await k.Requests.CancelAsync(request, k.AdminActor);
        Assert.Equal(GroupAddRequestStatus.Cancelled, (await k.Db.GroupAddRequests.AsNoTracking().SingleAsync(r => r.Id == request)).Status);
    }

    [SqlFact]
    public async Task Rows_tell_each_viewer_what_they_can_do()
    {
        await using var k = await ArrangeAsync();
        var person = await UserAsync(k.Db);
        var id = (await k.Requests.SubmitAsync(k.Default.Id, [person.Email], false, MembershipSource.Manual, k.AdminActor, null, null)).RequestId!.Value;
        var query = k.Db.GroupAddRequests.AsNoTracking().Where(r => r.Id == id);

        var asOwner = (await k.Requests.RowsAsync(query, k.Owner.Id, false)).Single();
        Assert.True(asOwner.CanDecide); Assert.False(asOwner.NeedsOverride);
        var asSuper = (await k.Requests.RowsAsync(query, k.SuperAdmin.Id, true)).Single();
        Assert.True(asSuper.CanDecide); Assert.True(asSuper.NeedsOverride);
        var asAdmin = (await k.Requests.RowsAsync(query, k.Admin.Id, false)).Single();
        Assert.False(asAdmin.CanDecide); Assert.True(asAdmin.IsRequester);
        Assert.Equal([k.Owner.DisplayName!], asOwner.Owners);
        Assert.Equal(person.Email, asOwner.Items.Single().Email);
    }
}
