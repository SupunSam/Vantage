namespace Vantage.Domain.Entities;

/// <summary>Usage events from the portal and, later, from Power BI activity and Tableau usage data.</summary>
public class DashboardView
{
    public long Id { get; set; }
    public int DashboardId { get; set; }
    public int? UserId { get; set; }
    public ViewSource Source { get; set; }
    public DateTime ViewedAtUtc { get; set; }
    public int? DurationSeconds { get; set; }
    /// <summary>Id of the event in the source system, so imports are not counted twice.</summary>
    public string? ExternalEventId { get; set; }
}

/// <summary>Append-only audit trail; kept permanently.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public int? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public int? DashboardId { get; set; }
    /// <summary>JSON with before/after values or other context.</summary>
    public string? Details { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    /// <summary>ServiceNow ticket the change was made under (free text, not checked against ServiceNow).</summary>
    public string? ServiceNowReference { get; set; }
}

public class Notification
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}

/// <summary>Outgoing email queue, sent by a background worker through SES (or the local mail catcher).</summary>
public class EmailOutbox
{
    public long Id { get; set; }
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string? Template { get; set; }
    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastError { get; set; }
}

public class JobRun
{
    public long Id { get; set; }
    public string JobName { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public JobRunStatus Status { get; set; }
    public string? Summary { get; set; }
    public JobTrigger Trigger { get; set; } = JobTrigger.Schedule;
    /// <summary>The Super Admin who pressed Run Now; null for scheduled runs.</summary>
    public int? TriggeredByUserId { get; set; }
}

/// <summary>
/// One row per scheduled job: whether it is paused, when it runs next and whether a run is in progress.
/// The scheduler claims a run by setting <see cref="RunningSinceUtc"/> with one atomic update, so two API instances never run the same job at once.
/// </summary>
public class JobState
{
    public string JobName { get; set; } = "";
    public bool IsPaused { get; set; }
    /// <summary>The cron text <see cref="NextRunAtUtc"/> was worked out from; when the setting changes the next run is recalculated.</summary>
    public string? Schedule { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    /// <summary>Set while a run is in progress. A claim older than the lease is treated as dead (the process stopped mid-run).</summary>
    public DateTime? RunningSinceUtc { get; set; }
}
