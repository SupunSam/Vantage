using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Jobs;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Tests;

/// <summary>The job runner (schedule, claim, record, audit) and the three jobs against a real database. Needs VANTAGE_TEST_SQL.</summary>
public class ScheduledJobTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A job whose schedule and behaviour the test controls.</summary>
    private sealed class FakeJob(string name, string? cron, Func<Task<JobOutcome>>? work = null) : IScheduledJob
    {
        public string Name => name;
        public string Title => "Fake " + name;
        public string Description => "test";
        public string? Cron { get; set; } = cron;
        public int Runs;
        public Task<string?> GetScheduleAsync(CancellationToken ct) => Task.FromResult(Cron);
        public async Task<JobOutcome> RunAsync(CancellationToken ct) { Runs++; return work is null ? new JobOutcome("did the work") : await work(); }
    }

    private static async Task<User> PersonAsync(AppDbContext db, UserStatus status = UserStatus.Active, string? employmentStatus = null)
    {
        var u = new User
        {
            Email = Unique("p") + "@rrd.com", DisplayName = Unique("Person "), UserType = UserType.Internal, Status = status, CreatedAtUtc = DateTime.UtcNow,
            HrmsProfile = employmentStatus is null ? null : new HrmsProfile { EmployeeId = Unique("E"), EmploymentStatus = employmentStatus, LastSyncedAtUtc = DateTime.UtcNow },
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task GiveRoleAsync(AppDbContext db, User user, string roleName)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == roleName);
        if (role is null)
        {
            role = new Role { Name = roleName, CreatedAtUtc = DateTime.UtcNow };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task SetSettingAsync(AppDbContext db, string key, string value)
    {
        var row = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = key });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    private static async Task<Dashboard> DashboardAsync(AppDbContext db, User owner, DateTime now, DateTime? published, DateTime? lastViewed, DashboardStatus status = DashboardStatus.Active)
    {
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var d = await new DashboardFactory(db, new OwnershipService(db, clock), audit, clock).CreateAsync(new Dashboard
        {
            Code = "JB", Name = Unique("Job dash "), Type = DashboardType.PowerBi, Status = status, PrimaryOwnerId = owner.Id,
        }, null, owner.Id);
        d.CreatedAtUtc = now.AddDays(-400);
        d.PublishedAtUtc = published;
        d.LastViewedAtUtc = lastViewed;
        await db.SaveChangesAsync();
        return d;
    }

    private JobRunner RunnerFor(AppDbContext db, FakeTimeProvider clock, params IScheduledJob[] jobs) =>
        new(db, jobs, new AuditWriter(db, new Ctx(), clock), new NotificationService(db, clock), clock, NullLogger<JobRunner>.Instance);

    // ------------------------------------------------------------ The runner

    [SqlFact]
    public async Task Run_now_records_the_run_audits_it_and_leaves_the_schedule_alone()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 0, 30, TimeSpan.Zero));
        var job = new FakeJob(Unique("job-"), "0 12 * * *");
        var runner = RunnerFor(db, clock, job);
        var admin = await PersonAsync(db);

        var before = (await runner.ListAsync()).Single();
        Assert.Equal(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), before.NextRunAtUtc);
        Assert.Null(before.LastRun);

        var result = await runner.RunNowAsync(job.Name, admin.Id);

        Assert.Equal("Succeeded", result.Run.Status);
        Assert.Equal("Manual", result.Run.Trigger);
        Assert.Equal(admin.DisplayName, result.Run.TriggeredBy);
        Assert.Equal("did the work", result.Run.Summary);
        var after = (await runner.ListAsync()).Single();
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);                      // a manual run doesn't move the schedule
        Assert.False(after.IsRunning);
        Assert.Equal(result.Run.Id, after.LastRun!.Id);
        var audited = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "job.run" && a.EntityId == result.Run.Id.ToString()).ToListAsync();
        Assert.Single(audited);
        Assert.Contains(job.Name, audited[0].Details);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.RunNowAsync("no-such-job", admin.Id));
    }

    [SqlFact]
    public async Task A_failing_job_is_recorded_as_failed_without_saving_its_half_done_work_and_super_admins_are_told()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var admin = await PersonAsync(db);
        await GiveRoleAsync(db, admin, SystemRoles.SuperAdmin);
        var ghost = Unique("ghost-");
        var job = new FakeJob(Unique("job-"), "0 12 * * *", () =>
        {
            db.Roles.Add(new Role { Name = ghost, CreatedAtUtc = DateTime.UtcNow });     // work that must not survive the failure
            throw new InvalidOperationException("the HR table is missing");
        });
        var runner = RunnerFor(db, clock, job);

        var result = await runner.RunNowAsync(job.Name, admin.Id);

        Assert.Equal("Failed", result.Run.Status);
        Assert.Contains("the HR table is missing", result.Run.Summary);
        Assert.False(await db.Roles.AnyAsync(r => r.Name == ghost));
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == admin.Id && n.Type == "job.failed" && n.Title.Contains(job.Name)));
        Assert.False((await runner.ListAsync()).Single().IsRunning);                    // the claim is released, so it can run again
        Assert.Equal("Failed", (await runner.RunNowAsync(job.Name, admin.Id)).Run.Status);   // not refused as "already running"
    }

    [SqlFact]
    public async Task A_job_that_is_already_running_is_refused_until_its_claim_goes_stale()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var job = new FakeJob(Unique("job-"), "0 12 * * *");
        var runner = RunnerFor(db, clock, job);
        await runner.ListAsync();                                                       // creates the state row
        await db.JobStates.Where(s => s.JobName == job.Name).ExecuteUpdateAsync(u => u.SetProperty(s => s.RunningSinceUtc, clock.GetUtcNow().UtcDateTime));

        var refused = await Assert.ThrowsAsync<RuleException>(() => runner.RunNowAsync(job.Name, null));
        Assert.Contains("already running", refused.Message);
        Assert.Equal(0, job.Runs);
        Assert.True((await runner.ListAsync()).Single().IsRunning);

        clock.Advance(JobRunner.Lease + TimeSpan.FromMinutes(1));                       // the process that held it must have died
        Assert.Equal("Succeeded", (await runner.RunNowAsync(job.Name, null)).Run.Status);
        Assert.Equal(1, job.Runs);
    }

    [SqlFact]
    public async Task The_scheduler_runs_a_due_job_once_then_moves_the_next_run_on()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 0, 30, TimeSpan.Zero));
        var job = new FakeJob(Unique("job-"), "0 12 * * *");
        var runner = RunnerFor(db, clock, job);

        Assert.Equal(0, await runner.RunDueAsync());                                    // first sight: schedules 12:00, runs nothing
        Assert.Equal(0, job.Runs);

        clock.SetUtcNow(new DateTimeOffset(2026, 10, 4, 12, 0, 10, TimeSpan.Zero));
        Assert.Equal(1, await runner.RunDueAsync());
        Assert.Equal(0, await runner.RunDueAsync());                                    // not twice
        Assert.Equal(1, job.Runs);
        var view = (await runner.ListAsync()).Single();
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), view.NextRunAtUtc);
        Assert.Equal("Schedule", view.LastRun!.Trigger);
        Assert.Null(view.LastRun.TriggeredBy);
    }

    [SqlFact]
    public async Task A_paused_job_is_skipped_and_resuming_does_not_catch_up_on_what_was_missed()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 0, 30, TimeSpan.Zero));
        var job = new FakeJob(Unique("job-"), "0 12 * * *");
        var runner = RunnerFor(db, clock, job);
        await runner.ListAsync();

        await runner.SetPausedAsync(job.Name, true);
        Assert.True((await runner.ListAsync()).Single().IsPaused);
        Assert.Null((await runner.ListAsync()).Single().NextRunAtUtc);                  // a paused job shows no next run
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, await runner.RunDueAsync());

        await runner.SetPausedAsync(job.Name, false);
        Assert.Equal(0, await runner.RunDueAsync());                                    // 12:00 passed while paused; it waits for tomorrow
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), (await runner.ListAsync()).Single().NextRunAtUtc);
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => (a.Action == "job.paused" || a.Action == "job.resumed") && a.EntityId == job.Name));
    }

    [SqlFact]
    public async Task Changing_the_schedule_setting_recalculates_the_next_run_and_no_schedule_means_none()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 0, 30, TimeSpan.Zero));
        var job = new FakeJob(Unique("job-"), "0 12 * * *");
        var runner = RunnerFor(db, clock, job);
        Assert.Equal(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), (await runner.ListAsync()).Single().NextRunAtUtc);

        job.Cron = "0 2 1 * *";
        Assert.Equal(new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc), (await runner.ListAsync()).Single().NextRunAtUtc);

        job.Cron = null;                                                                // e.g. the digest set to Never
        var off = (await runner.ListAsync()).Single();
        Assert.Null(off.NextRunAtUtc);
        Assert.Null(off.Schedule);
        clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(0, await runner.RunDueAsync());
    }

    [SqlFact]
    public async Task History_is_paged_newest_first_and_can_be_limited_to_one_job()
    {
        await using var db = fx.CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var a = new FakeJob(Unique("job-"), null);
        var b = new FakeJob(Unique("job-"), null);
        var runner = RunnerFor(db, clock, a, b);
        for (var i = 0; i < 3; i++) { await runner.RunNowAsync(a.Name, null); clock.Advance(TimeSpan.FromMinutes(1)); }
        await runner.RunNowAsync(b.Name, null);

        var (items, total) = await runner.HistoryAsync(a.Name, 1, 2);
        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].StartedAtUtc >= items[1].StartedAtUtc);
        Assert.All(items, i => Assert.Equal(a.Name, i.Job));
        Assert.Single((await runner.HistoryAsync(a.Name, 2, 2)).Items);
    }

    // ------------------------------------------------------------ HRMS job

    private sealed record HrmsKit(AppDbContext Db, HrmsSyncJob Job, JobRunner Runner);

    private HrmsKit HrmsKitFor(AppDbContext db)
    {
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var notifications = new NotificationService(db, clock);
        var groups = new GroupService(db, audit, clock);
        var access = new AccessGroupService(db, groups, notifications, audit, clock);
        var email = new EmailOutboxService(db, Options.Create(new EmailOptions()), clock);
        var requests = new GroupAddRequestService(db, access, notifications, email, audit, clock);
        var rules = new AccessGroupRuleService(db, access, requests, audit, clock);
        var job = new HrmsSyncJob(db, new HrmsSyncService(db, audit, clock), rules, new OwnerDepartureService(db, audit, notifications, email, clock), audit, notifications, clock, NullLogger<HrmsSyncJob>.Instance);
        return new HrmsKit(db, job, new JobRunner(db, [job], audit, notifications, clock, NullLogger<JobRunner>.Instance));
    }

    [SqlFact]
    public async Task The_hrms_job_ends_a_leavers_group_access_when_the_setting_is_on_and_keeps_it_when_off()
    {
        await using var db = fx.CreateContext();
        var k = HrmsKitFor(db);
        var owner = await PersonAsync(db);
        var dashboard = await DashboardAsync(db, owner, DateTime.UtcNow, DateTime.UtcNow, null);
        var group = await db.DashboardGroups.SingleAsync(g => g.DashboardId == dashboard.Id && g.IsDefault);
        var leaver = await PersonAsync(db, UserStatus.Inactive, "Inactive");           // HRMS says they left and the portal already shows Inactive
        var stayer = await PersonAsync(db, UserStatus.Active, "Active");
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, DashboardId = dashboard.Id, UserId = leaver.Id, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow },
            new GroupMember { GroupId = group.Id, DashboardId = dashboard.Id, UserId = stayer.Id, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await SetSettingAsync(db, SettingKeys.LeaverRevokeImmediately, "false");
        var off = await k.Runner.RunNowAsync(HrmsSyncJob.JobName, null);
        Assert.Equal("Succeeded", off.Run.Status);
        Assert.True(await LiveAsync(db, group.Id, leaver.Id));                          // off: they keep their memberships
        Assert.Equal(0, ((HrmsJobResult)off.Data!).LeaversRevoked);

        await SetSettingAsync(db, SettingKeys.LeaverRevokeImmediately, "true");
        var on = await k.Runner.RunNowAsync(HrmsSyncJob.JobName, null);
        var data = (HrmsJobResult)on.Data!;
        Assert.False(await LiveAsync(db, group.Id, leaver.Id));
        Assert.True(await LiveAsync(db, group.Id, stayer.Id));
        Assert.True(await LiveAsync(db, group.Id, owner.Id));                           // owners can't be removed
        Assert.True(data.LeaversRevoked >= 1 && data.MembershipsEnded >= 1);
        Assert.Contains("Access ended for", on.Run.Summary);
        var ended = await db.GroupMembers.AsNoTracking().SingleAsync(m => m.GroupId == group.Id && m.UserId == leaver.Id);
        Assert.Equal("Left the company (HRMS)", ended.RemovedReason);
        Assert.Null(ended.RemovedByUserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "user.access-revoked" && a.EntityId == leaver.Id.ToString()));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "job.run" && a.EntityId == on.Run.Id.ToString()));

        await k.Runner.RunNowAsync(HrmsSyncJob.JobName, null);                          // a second run finds nothing more to end for this leaver
        Assert.Equal(ended.RemovedAtUtc, (await db.GroupMembers.AsNoTracking().SingleAsync(m => m.GroupId == group.Id && m.UserId == leaver.Id)).RemovedAtUtc);
    }

    [SqlFact]
    public async Task A_leaver_who_still_owns_a_dashboard_keeps_that_access_and_super_admins_are_told_once()
    {
        await using var db = fx.CreateContext();
        var k = HrmsKitFor(db);
        var superAdmin = await PersonAsync(db);
        await GiveRoleAsync(db, superAdmin, SystemRoles.SuperAdmin);
        var ownerLeaver = await PersonAsync(db, UserStatus.Active, "Active");
        var dashboard = await DashboardAsync(db, ownerLeaver, DateTime.UtcNow, DateTime.UtcNow, null);
        var group = await db.DashboardGroups.SingleAsync(g => g.DashboardId == dashboard.Id && g.IsDefault);
        await SetSettingAsync(db, SettingKeys.LeaverRevokeImmediately, "true");

        // They are in HRMS as having left, and the portal marks them Inactive when the job runs.
        var email = ownerLeaver.Email;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO hrms.EmployeeProfile (EmployeeId, Email, FirstName, LastName, DisplayName, EmploymentStatus, HireDate, ExitDate)
            VALUES ({Unique("L")}, {email}, 'Owner', 'Leaver', 'Owner Leaver', 'Inactive', '2019-01-01', '2026-09-30')
            """);
        await k.Runner.RunNowAsync(HrmsSyncJob.JobName, null);

        Assert.Equal(UserStatus.Inactive, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerLeaver.Id)).Status);
        Assert.True(await LiveAsync(db, group.Id, ownerLeaver.Id));
        var orphan = await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == dashboard.Id);     // their only owner left, so it can't run its owner workflow (C43)
        Assert.Equal(DashboardStatus.Inactive, orphan.Status);
        Assert.True(orphan.OwnershipPendingReview);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == superAdmin.Id && n.Type == "hrms.leaver-owns-dashboards" && n.Body!.Contains(email)));

        await k.Runner.RunNowAsync(HrmsSyncJob.JobName, null);                          // no new leaver this time, so no second notice
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == superAdmin.Id && n.Type == "hrms.leaver-owns-dashboards" && n.Body!.Contains(email)));
    }

    private static Task<bool> LiveAsync(AppDbContext db, int groupId, int userId) =>
        db.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.RemovedAtUtc == null);

    // ------------------------------------------------------------ Inactivity job

    private InactivityJob InactivityFor(AppDbContext db)
    {
        var clock = TimeProvider.System;
        return new InactivityJob(db, new AuditWriter(db, new Ctx(), clock), new NotificationService(db, clock), new EmailOutboxService(db, Options.Create(new EmailOptions()), clock), clock);
    }

    [SqlFact]
    public async Task The_inactivity_job_flags_quiet_dashboards_and_tells_their_owners_once()
    {
        await using var db = fx.CreateContext();
        await SetSettingAsync(db, SettingKeys.InactivityDays, "90");
        var now = DateTime.UtcNow;
        var owner = await PersonAsync(db);
        var quiet = await DashboardAsync(db, owner, now, now.AddDays(-200), now.AddDays(-120));
        var neverOpened = await DashboardAsync(db, owner, now, now.AddDays(-150), null);
        var newlyPublished = await DashboardAsync(db, owner, now, now.AddDays(-5), null);
        var recent = await DashboardAsync(db, owner, now, now.AddDays(-200), now.AddDays(-10));
        var viewedByRow = await DashboardAsync(db, owner, now, now.AddDays(-200), now.AddDays(-120));
        db.DashboardViews.Add(new DashboardView { DashboardId = viewedByRow.Id, UserId = owner.Id, Source = ViewSource.PowerBiActivity, ViewedAtUtc = now.AddDays(-3) });
        var draft = await DashboardAsync(db, owner, now, now.AddDays(-200), null, DashboardStatus.Draft);
        await db.SaveChangesAsync();
        var job = InactivityFor(db);

        var outcome = await job.RunAsync(default);

        async Task<bool> Flagged(Dashboard d) => (await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).InactivityFlaggedAtUtc != null;
        Assert.True(await Flagged(quiet));
        Assert.True(await Flagged(neverOpened));
        Assert.False(await Flagged(newlyPublished));       // new: gets the full period
        Assert.False(await Flagged(recent));
        Assert.False(await Flagged(viewedByRow));          // an imported view row counts too
        Assert.False(await Flagged(draft));                // only Active dashboards
        Assert.Contains("Flagged", outcome.Summary);
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == owner.Id && n.Type == "dashboard.inactive" && n.Link == $"/dashboards/{quiet.Id}"));
        Assert.True(await db.EmailOutbox.AnyAsync(e => e.ToAddress == owner.Email && e.Subject.Contains(quiet.Name)));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "dashboard.inactivity-flagged" && a.DashboardId == quiet.Id));

        var notices = await db.Notifications.CountAsync(n => n.UserId == owner.Id && n.Type == "dashboard.inactive");
        await job.RunAsync(default);                                                    // already flagged: not told again
        Assert.Equal(notices, await db.Notifications.CountAsync(n => n.UserId == owner.Id && n.Type == "dashboard.inactive"));
    }

    [SqlFact]
    public async Task A_flagged_dashboard_that_is_opened_and_goes_quiet_again_is_flagged_again()
    {
        await using var db = fx.CreateContext();
        await SetSettingAsync(db, SettingKeys.InactivityDays, "90");
        var now = DateTime.UtcNow;
        var owner = await PersonAsync(db);
        var d = await DashboardAsync(db, owner, now, now.AddDays(-200), now.AddDays(-120));
        var job = InactivityFor(db);
        await job.RunAsync(default);
        Assert.NotNull((await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).InactivityFlaggedAtUtc);

        // Someone opens it (EmbedService clears the flag and stamps the view), and later it goes quiet again.
        var tracked = await db.Dashboards.SingleAsync(x => x.Id == d.Id);
        tracked.InactivityFlaggedAtUtc = null;
        tracked.LastViewedAtUtc = now.AddDays(-100);
        await db.SaveChangesAsync();
        await job.RunAsync(default);

        Assert.NotNull((await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).InactivityFlaggedAtUtc);
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.UserId == owner.Id && n.Type == "dashboard.inactive" && n.Link == $"/dashboards/{d.Id}"));
    }

    [SqlFact]
    public async Task The_inactivity_job_follows_the_threshold_setting_and_notes_dashboards_with_no_owner_to_tell()
    {
        await using var db = fx.CreateContext();
        var now = DateTime.UtcNow;
        var owner = await PersonAsync(db);
        var d = await DashboardAsync(db, owner, now, now.AddDays(-40), now.AddDays(-30));
        await using var jobDb = fx.CreateContext();                                      // a separate context, so it reads what the update below really saved
        var job = InactivityFor(jobDb);

        await SetSettingAsync(db, SettingKeys.InactivityDays, "90");
        await job.RunAsync(default);
        Assert.Null((await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).InactivityFlaggedAtUtc);   // 30 quiet days < 90

        await SetSettingAsync(db, SettingKeys.InactivityDays, "7");
        await db.Dashboards.Where(x => x.Id == d.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.PrimaryOwnerId, (int?)null));
        var outcome = await job.RunAsync(default);
        Assert.NotNull((await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).InactivityFlaggedAtUtc);
        Assert.Contains("no owner", outcome.Summary);
    }

    // ------------------------------------------------------------ New-hire digest

    private NewHireDigestJob DigestFor(AppDbContext db) =>
        new(db, new EmailOutboxService(db, Options.Create(new EmailOptions()), TimeProvider.System), TimeProvider.System);

    private static async Task<(string Email, string Name)> HireAsync(AppDbContext db, int daysAgo)
    {
        var email = Unique("hire") + "@rrd.com";
        var name = Unique("Hiree");
        var date = DateTime.UtcNow.Date.AddDays(-daysAgo);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO hrms.EmployeeProfile (EmployeeId, Email, FirstName, LastName, DisplayName, Department, JobTitle, EmploymentStatus, HireDate)
            VALUES ({Unique("H")}, {email}, {name}, 'Test', {name}, 'Finance', 'Analyst', 'Active', {date})
            """);
        return (email, name);
    }

    [SqlFact]
    public async Task The_digest_lists_recent_hires_for_super_admins_and_bpi_and_says_who_is_already_in_the_portal()
    {
        await using var db = fx.CreateContext();
        await SetSettingAsync(db, SettingKeys.NewHireDigestFrequency, "Monthly");
        var admin = await PersonAsync(db);
        await GiveRoleAsync(db, admin, SystemRoles.SuperAdmin);
        var bpi = await PersonAsync(db);
        await GiveRoleAsync(db, bpi, NewHireDigestJob.BpiRoleName);
        var bystander = await PersonAsync(db);
        var recent = await HireAsync(db, 3);
        var inPortal = await HireAsync(db, 10);
        var old = await HireAsync(db, 90);
        db.Users.Add(new User { Email = inPortal.Email, DisplayName = inPortal.Name, UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var outcome = await DigestFor(db).RunAsync(default);

        Assert.Contains("Sent the list of", outcome.Summary);
        foreach (var to in new[] { admin, bpi })
        {
            var mail = await db.EmailOutbox.AsNoTracking().Where(e => e.ToAddress == to.Email && e.Template == "new-hire-digest").SingleAsync();
            Assert.Contains(recent.Name, mail.HtmlBody);
            Assert.Contains(inPortal.Name, mail.HtmlBody);
            Assert.DoesNotContain(old.Name, mail.HtmlBody);
            Assert.Contains("Not yet", mail.HtmlBody);
        }
        Assert.False(await db.EmailOutbox.AnyAsync(e => e.ToAddress == bystander.Email));
    }

    [SqlFact]
    public async Task The_weekly_digest_looks_back_seven_days_and_sends_nothing_when_there_are_no_hires()
    {
        await using var db = fx.CreateContext();
        var admin = await PersonAsync(db);
        await GiveRoleAsync(db, admin, SystemRoles.SuperAdmin);
        var tenDaysAgo = await HireAsync(db, 10);
        await SetSettingAsync(db, SettingKeys.NewHireDigestFrequency, "Weekly");
        var sent = await db.EmailOutbox.CountAsync(e => e.Template == "new-hire-digest");

        var outcome = await DigestFor(db).RunAsync(default);
        // Other tests in this class may have added hires inside the window; what matters is that the 10-day-old hire is not listed.
        Assert.DoesNotContain(await db.EmailOutbox.AsNoTracking().Where(e => e.Template == "new-hire-digest").Select(e => e.HtmlBody).ToListAsync(), body => body.Contains(tenDaysAgo.Name));
        if (outcome.Summary.StartsWith("No one was hired")) Assert.Equal(sent, await db.EmailOutbox.CountAsync(e => e.Template == "new-hire-digest"));
    }

    [SqlFact]
    public async Task The_digest_set_to_never_has_no_schedule_and_sends_nothing()
    {
        await using var db = fx.CreateContext();
        await SetSettingAsync(db, SettingKeys.NewHireDigestFrequency, "Never");
        await HireAsync(db, 1);
        var sent = await db.EmailOutbox.CountAsync(e => e.Template == "new-hire-digest");
        var job = DigestFor(db);

        Assert.Null(await job.GetScheduleAsync(default));
        var outcome = await job.RunAsync(default);

        Assert.Contains("switched off", outcome.Summary);
        Assert.Equal(sent, await db.EmailOutbox.CountAsync(e => e.Template == "new-hire-digest"));

        await SetSettingAsync(db, SettingKeys.NewHireDigestFrequency, "Weekly");
        Assert.Equal(JobRules.WeeklyDigestCron, await job.GetScheduleAsync(default));
        await SetSettingAsync(db, SettingKeys.NewHireDigestFrequency, "Monthly");
        Assert.Equal(JobRules.MonthlyDigestCron, await job.GetScheduleAsync(default));
    }
}
