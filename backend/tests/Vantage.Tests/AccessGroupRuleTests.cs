using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Access group rules: HRMS-based proposals that only the owners can turn into real additions and removals (C29).</summary>
public class AccessGroupRuleTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, AccessGroupRuleService Rules, GroupAddRequestService Requests, AccessGroupService Access,
        User Owner, User SuperAdmin, Dashboard Dashboard, DashboardGroup Default, DashboardGroup North) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
        public GroupActor OwnerActor => new(Owner.Id, false);
        public GroupActor SuperActor => new(SuperAdmin.Id, true);
    }

    private async Task<Kit> ArrangeAsync(string? ownerDepartment = null)
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var groups = new GroupService(db, audit, clock);
        var notifications = new NotificationService(db, clock);
        var access = new AccessGroupService(db, groups, notifications, audit, clock);
        var email = new EmailOutboxService(db, Options.Create(new EmailOptions()), clock);
        var requests = new GroupAddRequestService(db, access, notifications, email, audit, clock);
        var owner = await PersonAsync(db, ownerDepartment, null);
        var super = await PersonAsync(db, null, null);
        var d = await new DashboardFactory(db, new OwnershipService(db, clock), audit, clock).CreateAsync(new Dashboard
        {
            Code = "AR", Name = Unique("Dash "), Type = DashboardType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = true, PrimaryOwnerId = owner.Id,
        }, "Region_All", owner.Id);
        var def = await db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault);
        var north = (await db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = d.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow })).Entity;
        await db.SaveChangesAsync();
        return new Kit(db, new AccessGroupRuleService(db, access, requests, audit, clock), requests, access, owner, super, d, def, north);
    }

    private static async Task<User> PersonAsync(AppDbContext db, string? department, string? location, UserStatus status = UserStatus.Active)
    {
        var u = new User
        {
            Email = Unique("p") + "@rrd.com", DisplayName = Unique("Person "), UserType = UserType.Internal, Status = status, CreatedAtUtc = DateTime.UtcNow,
            HrmsProfile = department is null && location is null ? null : new HrmsProfile { EmployeeId = Unique("E"), Department = department, Location = location, LastSyncedAtUtc = DateTime.UtcNow },
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static RuleInput Add(int groupId, string dept, bool move = false) =>
        new(groupId, Unique("Rule "), GroupChangeAction.Add, move, true, [new RuleCondition("Department", dept)]);

    private static RuleInput Remove(int groupId, string dept) =>
        new(groupId, Unique("Rule "), GroupChangeAction.Remove, false, true, [new RuleCondition("Department", dept)]);

    private static Task<bool> IsMemberAsync(AppDbContext db, int groupId, int userId) =>
        db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.RemovedAtUtc == null);

    [SqlFact]
    public async Task An_add_rule_sends_matching_people_to_the_owners_and_adds_nobody()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        var finance = new List<User> { await PersonAsync(k.Db, dept, null), await PersonAsync(k.Db, dept, null), await PersonAsync(k.Db, dept, null) };
        var already = await PersonAsync(k.Db, dept, null);
        await PersonAsync(k.Db, dept, null, UserStatus.Inactive);                 // leaver: never proposed
        await PersonAsync(k.Db, Unique("HR "), null);                              // different department
        await k.Access.AddMembersAsync(k.Default.Id, [already.Email], false, MembershipSource.Manual, k.SuperActor);

        var rule = await k.Rules.CreateAsync(Add(k.Default.Id, dept), k.SuperActor);
        var run = await k.Rules.RunAsync(rule.Id);

        Assert.Equal(3, run.Proposed);
        var request = await k.Db.GroupAddRequests.Include(r => r.Items).SingleAsync(r => r.Id == run.RequestId);
        Assert.Equal(GroupChangeAction.Add, request.Action);
        Assert.Null(request.RequestedByUserId);                                    // it comes from the rule, not a person
        Assert.Equal(rule.Name, request.RuleName);
        Assert.Equal(MembershipSource.AccessRule, request.Source);
        Assert.Equal(finance.Select(f => f.Id).Order(), request.Items.Select(i => i.UserId).Order());
        foreach (var f in finance) Assert.False(await IsMemberAsync(k.Db, k.Default.Id, f.Id));   // nothing happened yet
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "group-add.requested" && n.Title.Contains(rule.Name)));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "group.rule-run" && a.EntityId == k.Default.Id.ToString()));
        Assert.Contains("Sent 3 people", (await k.Db.AccessGroupRules.AsNoTracking().SingleAsync(r => r.Id == rule.Id)).LastRunSummary);

        var again = await k.Rules.RunAsync(rule.Id);                               // nothing new: they are already waiting
        Assert.Equal(0, again.Proposed);
    }

    [SqlFact]
    public async Task The_owner_approves_part_of_it_and_the_rejected_are_not_proposed_again_until_the_rule_changes()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        var people = new List<User> { await PersonAsync(k.Db, dept, null), await PersonAsync(k.Db, dept, null), await PersonAsync(k.Db, dept, null) };
        var rule = await k.Rules.CreateAsync(Add(k.Default.Id, dept), k.SuperActor);
        var run = await k.Rules.RunAsync(rule.Id);

        var decided = await k.Requests.DecideAsync(run.RequestId!.Value, [people[0].Id, people[1].Id], "Not the third", null, k.OwnerActor);

        Assert.Equal(GroupAddRequestStatus.PartlyApproved, decided.Status);
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, people[0].Id));
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, people[1].Id));
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, people[2].Id));
        Assert.Equal(MembershipSource.AccessRule, (await k.Db.GroupMembers.SingleAsync(m => m.UserId == people[0].Id && m.GroupId == k.Default.Id)).Source);

        Assert.Equal(0, (await k.Rules.RunAsync(rule.Id)).Proposed);               // the owner said no: not asked again
        var preview = await k.Rules.PreviewAsync(Add(k.Default.Id, dept), rule.Id);
        Assert.Contains(preview.Skipped, s => s.Email == people[2].Email && s.Detail!.Contains("rejected"));

        // Changing the rule makes it a new rule: the person is proposed afresh.
        await Task.Delay(30);
        await k.Rules.UpdateAsync(rule.Id, new RuleInput(k.Default.Id, rule.Name, GroupChangeAction.Add, true, true, [new RuleCondition("Department", dept)]), k.SuperActor);
        Assert.Equal(1, (await k.Rules.RunAsync(rule.Id)).Proposed);
    }

    [SqlFact]
    public async Task A_remove_rule_proposes_removals_never_the_owners_and_the_owner_decides()
    {
        var dept = Unique("Finance ");
        await using var k = await ArrangeAsync(ownerDepartment: dept);            // the owner is also in Finance
        var stay = await PersonAsync(k.Db, dept, null);
        var go = await PersonAsync(k.Db, dept, null);
        var other = await PersonAsync(k.Db, Unique("HR "), null);
        await k.Access.AddMembersAsync(k.Default.Id, [stay.Email, go.Email, other.Email], false, MembershipSource.Manual, k.SuperActor);
        var rule = await k.Rules.CreateAsync(Remove(k.Default.Id, dept), k.SuperActor);

        var preview = await k.Rules.PreviewAsync(Remove(k.Default.Id, dept), null);
        Assert.Contains(preview.Skipped, s => s.Email == k.Owner.Email && s.Detail!.Contains("Owns"));
        var run = await k.Rules.RunAsync(rule.Id);
        Assert.Equal(2, run.Proposed);                                             // stay and go, not the owner, not the HR person

        var request = await k.Db.GroupAddRequests.AsNoTracking().SingleAsync(r => r.Id == run.RequestId);
        Assert.Equal(GroupChangeAction.Remove, request.Action);
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, go.Id));               // nothing removed yet

        var decided = await k.Requests.DecideAsync(run.RequestId!.Value, [go.Id], null, null, k.OwnerActor);

        Assert.Equal(GroupAddRequestStatus.PartlyApproved, decided.Status);
        Assert.False(await IsMemberAsync(k.Db, k.Default.Id, go.Id));
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, stay.Id));
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, other.Id));
        Assert.True(await IsMemberAsync(k.Db, k.Default.Id, k.Owner.Id));
        var ended = await k.Db.GroupMembers.SingleAsync(m => m.UserId == go.Id && m.GroupId == k.Default.Id);
        Assert.Contains(rule.Name, ended.RemovedReason);
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == go.Id && n.Type == "access.removed"));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "group.remove-approved" && a.EntityId == k.Default.Id.ToString()));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "group.member-removed" && a.EntityId == k.Default.Id.ToString() && a.Details!.Contains(go.Email)));
    }

    [SqlFact]
    public async Task People_in_another_group_are_proposed_only_when_the_rule_may_move_them()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        var person = await PersonAsync(k.Db, dept, null);
        await k.Access.AddMembersAsync(k.Default.Id, [person.Email], false, MembershipSource.Manual, k.SuperActor);

        var stay = await k.Rules.PreviewAsync(Add(k.North.Id, dept, move: false), null);
        Assert.Empty(stay.WouldPropose);
        Assert.Contains(stay.Skipped, s => s.Email == person.Email && s.Detail!.Contains("Already in"));

        var moving = await k.Rules.PreviewAsync(Add(k.North.Id, dept, move: true), null);
        Assert.Equal([person.Email], moving.WouldPropose.Select(p => p.Email));
        Assert.Contains("Moved from", moving.WouldPropose.Single().Detail);
    }

    [SqlFact]
    public async Task A_person_matching_an_add_rule_and_a_remove_rule_of_the_same_group_is_left_for_a_person_to_decide()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        var loc = Unique("Colombo ");
        var both = await PersonAsync(k.Db, dept, loc);
        var onlyAdd = await PersonAsync(k.Db, dept, Unique("Kandy "));
        await k.Rules.CreateAsync(new RuleInput(k.North.Id, "Leave Colombo", GroupChangeAction.Remove, false, true, [new RuleCondition("Location", loc)]), k.SuperActor);

        var preview = await k.Rules.PreviewAsync(Add(k.North.Id, dept), null);

        Assert.Equal([onlyAdd.Email], preview.WouldPropose.Select(p => p.Email));
        Assert.Contains(preview.Skipped, s => s.Email == both.Email && s.Detail!.Contains("opposite"));
    }

    [SqlFact]
    public async Task Rules_are_checked_and_only_Super_Admins_manage_them()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");

        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(Add(k.North.Id, dept), k.OwnerActor));                              // not a Super Admin
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(new RuleInput(k.North.Id, "x", GroupChangeAction.Add, false, true, []), k.SuperActor));
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(new RuleInput(k.North.Id, "x", GroupChangeAction.Add, false, true, [new RuleCondition("ShoeSize", "9")]), k.SuperActor));
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(new RuleInput(k.North.Id, "x", GroupChangeAction.Add, false, true, [new RuleCondition("Department", "A"), new RuleCondition("department", "B")]), k.SuperActor));
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(new RuleInput(k.North.Id, "x", GroupChangeAction.Add, false, true, [new RuleCondition("Department", "  ")]), k.SuperActor));

        var rule = await k.Rules.CreateAsync(Add(k.North.Id, dept), k.SuperActor);
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.CreateAsync(new RuleInput(k.North.Id, rule.Name, GroupChangeAction.Add, false, true, [new RuleCondition("Division", "X")]), k.SuperActor));   // name taken
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.UpdateAsync(rule.Id, Add(k.North.Id, dept), k.OwnerActor));
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.DeleteAsync(rule.Id, k.OwnerActor));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "group.rule-created" && a.EntityId == k.North.Id.ToString() && a.Details!.Contains(dept)));   // shows in the group's history
    }

    [SqlFact]
    public async Task Switched_off_rules_do_not_run_and_paused_dashboards_are_skipped()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        await PersonAsync(k.Db, dept, null);
        var rule = await k.Rules.CreateAsync(Add(k.North.Id, dept), k.SuperActor);

        await k.Rules.SetActiveAsync(rule.Id, false, k.SuperActor);
        await Assert.ThrowsAsync<RuleException>(() => k.Rules.RunAsync(rule.Id));
        Assert.DoesNotContain(await k.Rules.RunAllAsync(), r => r.RuleId == rule.Id);

        await k.Rules.SetActiveAsync(rule.Id, true, k.SuperActor);
        var dash = await k.Db.Dashboards.SingleAsync(d => d.Id == k.Dashboard.Id);
        dash.OwnershipPendingReview = true;
        await k.Db.SaveChangesAsync();
        var paused = await k.Rules.RunAsync(rule.Id);
        Assert.Equal(0, paused.Proposed);
        Assert.Contains("under review", paused.Message);
        Assert.False(await k.Db.GroupAddRequests.AnyAsync(r => r.RuleId == rule.Id));
    }

    [SqlFact]
    public async Task Running_all_rules_of_one_group_leaves_other_groups_rules_alone()
    {
        await using var k = await ArrangeAsync();
        var deptA = Unique("Finance ");
        var deptB = Unique("Sales ");
        await PersonAsync(k.Db, deptA, null);
        await PersonAsync(k.Db, deptB, null);
        var onDefault = await k.Rules.CreateAsync(Add(k.Default.Id, deptA), k.SuperActor);
        var onNorth = await k.Rules.CreateAsync(Add(k.North.Id, deptB), k.SuperActor);

        var results = await k.Rules.RunAllAsync(default, k.North.Id);

        Assert.Equal([onNorth.Id], results.Select(r => r.RuleId));
        Assert.True(await k.Db.GroupAddRequests.AnyAsync(r => r.RuleId == onNorth.Id));
        Assert.False(await k.Db.GroupAddRequests.AnyAsync(r => r.RuleId == onDefault.Id));
        Assert.Contains(onDefault.Id, (await k.Rules.RunAllAsync()).Select(r => r.RuleId));   // without a group, every rule runs
    }

    [SqlFact]
    public async Task Deleting_a_rule_keeps_the_request_it_raised_and_its_name()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        var person = await PersonAsync(k.Db, dept, null);
        var rule = await k.Rules.CreateAsync(Add(k.North.Id, dept), k.SuperActor);
        var run = await k.Rules.RunAsync(rule.Id);

        await k.Rules.DeleteAsync(rule.Id, k.SuperActor);

        var request = await k.Db.GroupAddRequests.AsNoTracking().SingleAsync(r => r.Id == run.RequestId);
        Assert.Null(request.RuleId);
        Assert.Equal(rule.Name, request.RuleName);
        await k.Requests.DecideAsync(request.Id, [person.Id], null, null, k.OwnerActor);   // the owners can still decide it
        Assert.True(await IsMemberAsync(k.Db, k.North.Id, person.Id));
    }

    [SqlFact]
    public async Task Rows_show_a_rule_request_as_coming_from_the_rule()
    {
        await using var k = await ArrangeAsync();
        var dept = Unique("Finance ");
        await PersonAsync(k.Db, dept, null);
        var rule = await k.Rules.CreateAsync(Add(k.North.Id, dept), k.SuperActor);
        var run = await k.Rules.RunAsync(rule.Id);

        var row = (await k.Requests.RowsAsync(k.Db.GroupAddRequests.AsNoTracking().Where(r => r.Id == run.RequestId), k.Owner.Id, false)).Single();

        Assert.Null(row.RequestedBy);
        Assert.Equal(rule.Name, row.RuleName);
        Assert.Equal("Add", row.Action);
        Assert.True(row.CanDecide);
        Assert.False(row.NeedsOverride);
    }
}
