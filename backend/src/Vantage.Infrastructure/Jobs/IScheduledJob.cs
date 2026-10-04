namespace Vantage.Infrastructure.Jobs;

/// <summary>What a run produced: a one-line summary for the Scheduled Jobs page and the audit log, plus optional detail for the caller.</summary>
public sealed record JobOutcome(string Summary, object? Data = null);

/// <summary>
/// A background job. <see cref="JobRunner"/> decides when it runs, records every run and audits it; the job only does the work.
/// Jobs are scoped services, so they use the database like any other service.
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable key stored in JobRuns and JobStates ("hrms-sync"). Never rename one once deployed.</summary>
    string Name { get; }
    string Title { get; }
    string Description { get; }

    /// <summary>Five-field cron (UTC) for the next runs, read from the job's setting, or null when the job is switched off.</summary>
    Task<string?> GetScheduleAsync(CancellationToken ct);

    Task<JobOutcome> RunAsync(CancellationToken ct);
}
