using Vantage.Domain.Entities;

namespace Vantage.Infrastructure.Jobs;

/// <summary>The decisions the scheduled jobs make, kept free of the database so they are unit tested directly.</summary>
public static class JobRules
{
    /// <summary>Schedules for the jobs whose timing is fixed in code (the HRMS job's comes from its setting).</summary>
    public const string InactivityCron = "0 3 * * *";      // every day at 03:00 UTC
    public const string WeeklyDigestCron = "0 6 * * 1";    // Mondays at 06:00 UTC
    public const string MonthlyDigestCron = "0 6 1 * *";   // the 1st of the month at 06:00 UTC

    /// <summary>The cron text for the new-hire digest setting, or null when it is switched off ("Never") or unrecognised.</summary>
    public static string? DigestCron(string? frequency) => frequency?.Trim().ToLowerInvariant() switch
    {
        "weekly" => WeeklyDigestCron,
        "monthly" => MonthlyDigestCron,
        _ => null,
    };

    /// <summary>The first hire date that counts as new for a digest: 7 days back for Weekly, 31 for Monthly.</summary>
    public static DateOnly NewHireWindowStart(string? frequency, DateOnly today) =>
        today.AddDays(string.Equals(frequency?.Trim(), "Weekly", StringComparison.OrdinalIgnoreCase) ? -7 : -31);

    /// <summary>
    /// Whether a dashboard has gone quiet. The last view is what counts; one never viewed counts from when it was published
    /// (or created), so a new dashboard gets the full period. A dashboard already flagged is not flagged again until someone views it
    /// (opening it clears the flag).
    /// </summary>
    public static bool IsInactive(DateTime? lastViewedUtc, DateTime? publishedUtc, DateTime createdUtc, DateTime nowUtc, int days, bool alreadyFlagged)
    {
        if (alreadyFlagged) return false;
        var since = lastViewedUtc ?? publishedUtc ?? createdUtc;
        return since <= nowUtc.AddDays(-days);
    }

    public static int DaysQuiet(DateTime? lastViewedUtc, DateTime? publishedUtc, DateTime createdUtc, DateTime nowUtc) =>
        (int)(nowUtc - (lastViewedUtc ?? publishedUtc ?? createdUtc)).TotalDays;

    /// <summary>The later of the dashboard's last-viewed time and its newest view row (imports can add views the dashboard row doesn't know about).</summary>
    public static DateTime? LatestView(DateTime? lastViewedUtc, DateTime? newestViewRowUtc) =>
        lastViewedUtc is { } a && newestViewRowUtc is { } b ? (a > b ? a : b) : lastViewedUtc ?? newestViewRowUtc;

    /// <summary>A person who has left, by HRMS status: anything other than Active.</summary>
    public static bool HasLeft(HrmsProfile? profile) => profile is not null && !string.Equals(profile.EmploymentStatus, "Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Ran 3 of them" style plural helper for the one-line run summaries.</summary>
    public static string Plural(int n, string singular, string? plural = null) => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";
}
