using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Infrastructure.Jobs;

public sealed record JobRunView(long Id, string Job, string Title, DateTime StartedAtUtc, DateTime? FinishedAtUtc, string Status, string Trigger, string? TriggeredBy, string? Summary);

public sealed record JobView(string Name, string Title, string Description, string? Schedule, bool IsPaused, bool IsRunning, DateTime? NextRunAtUtc, JobRunView? LastRun);

public sealed record JobRunResult(JobRunView Run, object? Data);

/// <summary>
/// Decides when each job runs, runs it, records the run and audits it. The same code serves the scheduler (every 30 seconds)
/// and Run Now, so a manual run behaves exactly like a scheduled one.
/// </summary>
public sealed class JobRunner(AppDbContext db, IEnumerable<IScheduledJob> jobs, AuditWriter audit, NotificationService notifications,
    TimeProvider clock, ILogger<JobRunner> log)
{
    /// <summary>A run that has been "in progress" longer than this is assumed dead (the process stopped), and the job may run again.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private IScheduledJob Find(string name) => jobs.FirstOrDefault(j => j.Name == name) ?? throw new KeyNotFoundException();

    // ------------------------------------------------------------ Reading

    public async Task<List<JobView>> ListAsync(CancellationToken ct = default)
    {
        var views = new List<JobView>();
        foreach (var job in jobs.OrderBy(j => j.Title))
        {
            var state = await EnsureStateAsync(job, ct);
            var last = await db.JobRuns.AsNoTracking().Where(r => r.JobName == job.Name).OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            views.Add(new JobView(job.Name, job.Title, job.Description, state.Schedule, state.IsPaused, IsRunning(state), state.IsPaused ? null : state.NextRunAtUtc,
                last is null ? null : (await ToViewsAsync([last], ct))[0]));
        }
        return views;
    }

    public async Task<(List<JobRunView> Items, int Total)> HistoryAsync(string? jobName, int page, int pageSize, CancellationToken ct = default)
    {
        var q = db.JobRuns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(jobName)) q = q.Where(r => r.JobName == jobName);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.Id).Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (await ToViewsAsync(rows, ct), total);
    }

    // ------------------------------------------------------------ Control

    public async Task SetPausedAsync(string name, bool paused, CancellationToken ct = default)
    {
        var job = Find(name);
        var state = await EnsureStateAsync(job, ct);
        if (state.IsPaused == paused) return;
        var next = state.NextRunAtUtc;
        if (!paused) next = NextRun(state.Schedule); // no catch-up run for the time it was paused
        await db.JobStates.Where(s => s.JobName == name).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsPaused, paused).SetProperty(s => s.NextRunAtUtc, next), ct);
        audit.Add(paused ? "job.paused" : "job.resumed", "JobState", name, details: new { job = name, title = job.Title });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs a job now, whatever its schedule or pause state. Refused while it is already running.</summary>
    public async Task<JobRunResult> RunNowAsync(string name, int? userId, CancellationToken ct = default)
    {
        var job = Find(name);
        await EnsureStateAsync(job, ct);
        return await ExecuteAsync(job, JobTrigger.Manual, userId, ct)
            ?? throw new RuleException($"{job.Title} is already running. Wait for it to finish, then try again.");
    }

    /// <summary>The scheduler's tick: runs every job whose time has come. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var ran = 0;
        foreach (var job in jobs)
        {
            try
            {
                var state = await EnsureStateAsync(job, ct);
                if (state.IsPaused || state.Schedule is null || state.NextRunAtUtc is not { } due || due > Now) continue;
                if (await ExecuteAsync(job, JobTrigger.Schedule, null, ct) is not null) ran++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduled job {Job} could not be started", job.Name);
                db.ChangeTracker.Clear();
            }
        }
        return ran;
    }

    // ------------------------------------------------------------ Running

    /// <summary>Null when another run holds the job.</summary>
    private async Task<JobRunResult?> ExecuteAsync(IScheduledJob job, JobTrigger trigger, int? userId, CancellationToken ct)
    {
        var started = Now;
        var claimed = await db.JobStates.Where(s => s.JobName == job.Name && (s.RunningSinceUtc == null || s.RunningSinceUtc < started - Lease))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RunningSinceUtc, started), ct);
        if (claimed == 0) return null;

        var run = new JobRun { JobName = job.Name, StartedAtUtc = started, Status = JobRunStatus.Running, Trigger = trigger, TriggeredByUserId = userId };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var runId = run.Id;

        JobOutcome? outcome = null;
        string? failure = null;
        try { outcome = await job.RunAsync(ct); }
        catch (OperationCanceledException) { failure = "Stopped because the portal was shutting down."; }
        catch (Exception ex)
        {
            log.LogError(ex, "Scheduled job {Job} failed", job.Name);
            failure = ex is RuleException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        }

        // Start clean: after a failure the context may hold half-finished changes that must not be saved with the run record.
        if (failure is not null) db.ChangeTracker.Clear();
        run = await db.JobRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);
        run.FinishedAtUtc = Now;
        run.Status = failure is null ? JobRunStatus.Succeeded : JobRunStatus.Failed;
        run.Summary = Clip(failure ?? outcome!.Summary, 3900);
        audit.Add("job.run", "JobRun", run.Id, details: new { job = job.Name, title = job.Title, trigger = trigger.ToString(), status = run.Status.ToString(), summary = run.Summary });
        if (failure is not null) await NotifyFailureAsync(job, run.Summary!);
        await db.SaveChangesAsync(CancellationToken.None);

        // Release the claim and, for a scheduled run, move the next run on. A manual run leaves the schedule alone.
        var state = await db.JobStates.AsNoTracking().SingleAsync(s => s.JobName == job.Name, CancellationToken.None);
        var next = trigger == JobTrigger.Schedule ? NextRun(state.Schedule) : state.NextRunAtUtc;
        await db.JobStates.Where(x => x.JobName == job.Name)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.RunningSinceUtc, (DateTime?)null).SetProperty(x => x.NextRunAtUtc, next), CancellationToken.None);

        return new JobRunResult((await ToViewsAsync([run], CancellationToken.None))[0], outcome?.Data);
    }

    private async Task NotifyFailureAsync(IScheduledJob job, string summary)
    {
        var admins = await db.UserRoles.Where(r => r.Role.Name == SystemRoles.SuperAdmin && r.User.Status == UserStatus.Active).Select(r => r.UserId).ToListAsync();
        notifications.Notify(admins, "job.failed", $"{job.Title} failed", summary, "/jobs");
    }

    /// <summary>
    /// The job's state row (a fresh, untracked copy), created on first sight, with the next run recalculated whenever its schedule setting changed.
    /// State is changed only with direct updates, so a copy held in the change tracker can never go stale or overwrite another instance's claim.
    /// </summary>
    private async Task<JobState> EnsureStateAsync(IScheduledJob job, CancellationToken ct)
    {
        var cron = await job.GetScheduleAsync(ct);
        var state = await db.JobStates.AsNoTracking().SingleOrDefaultAsync(s => s.JobName == job.Name, ct);
        if (state is null)
        {
            state = new JobState { JobName = job.Name, Schedule = cron, NextRunAtUtc = NextRun(cron) };
            db.JobStates.Add(state);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) // another API instance created it first
            {
                db.Entry(state).State = EntityState.Detached;
                state = await db.JobStates.AsNoTracking().SingleAsync(s => s.JobName == job.Name, ct);
            }
            db.Entry(state).State = EntityState.Detached;
        }
        else if (state.Schedule != cron || (cron is not null && state.NextRunAtUtc is null))
        {
            var next = NextRun(cron);
            await db.JobStates.Where(s => s.JobName == job.Name).ExecuteUpdateAsync(u => u.SetProperty(s => s.Schedule, cron).SetProperty(s => s.NextRunAtUtc, next), ct);
            state.Schedule = cron;
            state.NextRunAtUtc = next;
        }
        return state;
    }

    private DateTime? NextRun(string? cron) => cron is not null && CronSchedule.TryParse(cron, out var s, out _) ? s!.Next(Now) : null;

    private bool IsRunning(JobState state) => state.RunningSinceUtc is { } since && since >= Now - Lease;

    private async Task<List<JobRunView>> ToViewsAsync(List<JobRun> runs, CancellationToken ct)
    {
        var ids = runs.Where(r => r.TriggeredByUserId != null).Select(r => r.TriggeredByUserId!.Value).Distinct().ToList();
        var names = ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email, ct);
        return runs.Select(r => new JobRunView(r.Id, r.JobName, jobs.FirstOrDefault(j => j.Name == r.JobName)?.Title ?? r.JobName, r.StartedAtUtc, r.FinishedAtUtc,
            r.Status.ToString(), r.Trigger.ToString(), r.TriggeredByUserId is { } id && names.TryGetValue(id, out var n) ? n : null, r.Summary)).ToList();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
