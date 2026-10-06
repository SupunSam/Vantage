using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Access requests: raising, owner approval into a chosen group, rejection, and the emails they queue.</summary>
public class AccessRequestTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, AccessRequestService Requests, GroupService Groups, User Owner, User Requester) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
        public GroupActor OwnerActor => new(Owner.Id, false);
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var groups = new GroupService(db, audit, clock);
        var notifications = new NotificationService(db, clock);
        var access = new AccessGroupService(db, groups, notifications, audit, clock);
        var email = new EmailOutboxService(db, Options.Create(new EmailOptions()), clock);
        var owner = await UserAsync(db);
        var requester = await UserAsync(db);
        return new Kit(db, new AccessRequestService(db, access, notifications, email, audit, clock), groups, owner, requester);
    }

    private static async Task<User> UserAsync(AppDbContext db, UserType type = UserType.Internal)
    {
        var u = new User { Email = Unique("u") + (type == UserType.Internal ? "@rrd.com" : "@client.com"), DisplayName = Unique("User "), UserType = type, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<(Dashboard D, DashboardGroup Default)> DashboardAsync(Kit k, bool rls, Audience audience = Audience.Client)
    {
        var factory = new DashboardFactory(k.Db, new OwnershipService(k.Db, TimeProvider.System), new AuditWriter(k.Db, new Ctx(), TimeProvider.System), TimeProvider.System);
        var d = await factory.CreateAsync(new Dashboard
        {
            Code = "AR", Name = Unique("Dash "), Type = BiType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = rls, PrimaryOwnerId = k.Owner.Id, Audience = audience,
        }, rls ? "Region_All" : null, k.Owner.Id);
        return (d, await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault));
    }

    [SqlFact]
    public async Task A_request_notifies_and_emails_the_owners_and_only_one_can_be_pending()
    {
        await using var k = await ArrangeAsync();
        var (d, _) = await DashboardAsync(k, rls: false);

        var r = await k.Requests.CreateAsync(d.Id, k.Requester.Id, "Need it for the Q3 review");

        Assert.Equal(AccessRequestStatus.Pending, r.Status);
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "access-request.created"));
        var mail = await k.Db.EmailOutbox.SingleAsync(e => e.ToAddress == k.Owner.Email);
        Assert.Contains("Need it for the Q3 review", mail.HtmlBody);
        Assert.Equal(EmailStatus.Pending, mail.Status);
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.CreateAsync(d.Id, k.Requester.Id, null));
    }

    [SqlFact]
    public async Task Approving_without_rls_puts_the_requester_in_the_default_group()
    {
        await using var k = await ArrangeAsync();
        var (d, def) = await DashboardAsync(k, rls: false);
        var r = await k.Requests.CreateAsync(d.Id, k.Requester.Id, null);

        await k.Requests.ApproveAsync(r.Id, null, "Welcome", k.OwnerActor);

        k.Db.ChangeTracker.Clear();
        var saved = await k.Db.AccessRequests.SingleAsync(x => x.Id == r.Id);
        Assert.Equal((AccessRequestStatus.Approved, (int?)def.Id, "Welcome"), (saved.Status, saved.AssignedGroupId, saved.DecisionNote));
        var member = await k.Db.GroupMembers.SingleAsync(m => m.DashboardId == d.Id && m.UserId == k.Requester.Id && m.RemovedAtUtc == null);
        Assert.Equal(MembershipSource.AccessRequest, member.Source);
        Assert.True(await k.Db.EmailOutbox.AnyAsync(e => e.ToAddress == k.Requester.Email && e.Template == "access-request.approved"));
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.CreateAsync(d.Id, k.Requester.Id, null)); // already has access
    }

    [SqlFact]
    public async Task On_rls_dashboards_the_approver_must_pick_a_group()
    {
        await using var k = await ArrangeAsync();
        var (d, _) = await DashboardAsync(k, rls: true);
        var north = await k.Groups.AddAsync(d.Id, Unique("N"), "Region_North", k.Owner.Id);
        var r = await k.Requests.CreateAsync(d.Id, k.Requester.Id, null);

        await Assert.ThrowsAsync<RuleException>(() => k.Requests.ApproveAsync(r.Id, null, null, k.OwnerActor));
        await k.Requests.ApproveAsync(r.Id, north.Id, null, k.OwnerActor);

        Assert.Equal(north.Id, await k.Db.GroupMembers.Where(m => m.DashboardId == d.Id && m.UserId == k.Requester.Id && m.RemovedAtUtc == null).Select(m => m.GroupId).SingleAsync());
    }

    [SqlFact]
    public async Task Only_owners_or_super_admins_decide_and_rejection_tells_the_requester()
    {
        await using var k = await ArrangeAsync();
        var (d, _) = await DashboardAsync(k, rls: false);
        var stranger = await UserAsync(k.Db);
        var r = await k.Requests.CreateAsync(d.Id, k.Requester.Id, null);

        await Assert.ThrowsAsync<RuleException>(() => k.Requests.RejectAsync(r.Id, "No", new GroupActor(stranger.Id, false)));
        var superAdmin = new GroupActor(stranger.Id, IsSuperAdmin: true);
        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Requests.RejectAsync(r.Id, "Use the team dashboard instead", superAdmin));   // stepping in for the owners needs a reason
        Assert.Contains("reason", ex.Message);
        await k.Requests.RejectAsync(r.Id, "Use the team dashboard instead", superAdmin, default, "Both owners are on leave");
        var saved = await k.Db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        Assert.Equal("Both owners are on leave", saved.OverrideReason);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "access-request.rejected" && a.EntityId == r.Id.ToString() && a.Details!.Contains("Both owners are on leave")));
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "access-request.overridden" && n.Body!.Contains("Both owners are on leave")));

        Assert.Equal(AccessRequestStatus.Rejected, (await k.Db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status);
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Requester.Id && n.Type == "access-request.rejected"));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.EntityType == "AccessRequest" && a.EntityId == r.Id.ToString() && a.Action == "access-request.rejected"));
        await Assert.ThrowsAsync<RuleException>(() => k.Requests.ApproveAsync(r.Id, null, null, k.OwnerActor)); // already decided
    }

    [SqlFact]
    public async Task Requests_are_refused_while_the_dashboard_has_no_confirmed_owner()
    {
        await using var k = await ArrangeAsync();
        var (d, _) = await DashboardAsync(k, rls: false);
        await k.Db.Dashboards.Where(x => x.Id == d.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OwnershipPendingReview, true));
        k.Db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Requests.CreateAsync(d.Id, k.Requester.Id, null));
        Assert.Equal(AccessRequestService.OwnerlessMessage, ex.Message);
    }

    [SqlFact]
    public async Task A_requester_can_withdraw_a_pending_request()
    {
        await using var k = await ArrangeAsync();
        var (d, _) = await DashboardAsync(k, rls: false);
        var r = await k.Requests.CreateAsync(d.Id, k.Requester.Id, null);

        await k.Requests.CancelAsync(r.Id, k.Requester.Id);

        Assert.Equal(AccessRequestStatus.Cancelled, (await k.Db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id)).Status);
        var again = await k.Requests.CreateAsync(d.Id, k.Requester.Id, null); // a new one is allowed after withdrawing
        Assert.NotEqual(r.Id, again.Id);
    }
}
