using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Jobs;

/// <summary>
/// Daily check (C4): an Active dashboard nobody has opened for the configured number of days is flagged and its owners are told once.
/// It only flags and emails; it never changes the dashboard's status. Opening the dashboard clears the flag (EmbedService), and a
/// Super Admin preview is not a view (C27), so previews never reset it.
/// </summary>
public sealed class InactivityJob(AppDbContext db, AuditWriter audit, NotificationService notifications, EmailOutboxService email, TimeProvider clock) : IScheduledJob
{
    public const string JobName = "inactivity-check";
    public string Name => JobName;
    public string Title => "Inactivity Check";
    public string Description => "Flags Active dashboards that nobody has opened for the configured number of days and emails their owners once.";

    public Task<string?> GetScheduleAsync(CancellationToken ct) => Task.FromResult<string?>(JobRules.InactivityCron);

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var days = await JobSettings.GetIntAsync(db, SettingKeys.InactivityDays, ct);
        var cutoff = now.AddDays(-days);

        // A cheap first cut in SQL: only dashboards whose own dates are old enough. Then the exact rule, including the newest view row.
        var candidates = await db.Dashboards
            .Where(d => d.Status == DashboardStatus.Active && d.InactivityFlaggedAtUtc == null
                        && (d.LastViewedAtUtc ?? d.PublishedAtUtc ?? d.CreatedAtUtc) <= cutoff)
            .ToListAsync(ct);
        var ids = candidates.Select(d => d.Id).ToList();
        var newestViews = await db.DashboardViews.Where(v => ids.Contains(v.DashboardId))
            .GroupBy(v => v.DashboardId).Select(g => new { Id = g.Key, Newest = g.Max(v => v.ViewedAtUtc) }).ToDictionaryAsync(x => x.Id, x => x.Newest, ct);

        int flagged = 0, withoutOwner = 0, emailed = 0;
        foreach (var d in candidates)
        {
            var last = JobRules.LatestView(d.LastViewedAtUtc, newestViews.TryGetValue(d.Id, out var n) ? n : null);
            if (!JobRules.IsInactive(last, d.PublishedAtUtc, d.CreatedAtUtc, now, days, alreadyFlagged: false)) continue;

            d.InactivityFlaggedAtUtc = now;
            flagged++;
            var quiet = JobRules.DaysQuiet(last, d.PublishedAtUtc, d.CreatedAtUtc, now);
            audit.Add("dashboard.inactivity-flagged", "Dashboard", d.Id, d.Id, new { daysQuiet = quiet, thresholdDays = days, lastViewedAtUtc = last });

            var owners = new[] { d.PrimaryOwnerId, d.BackupOwnerId }.Where(o => o != null).Select(o => o!.Value).Distinct().ToList();
            if (owners.Count == 0) { withoutOwner++; continue; }
            var title = $"{d.Name} has not been opened for {quiet} days";
            notifications.Notify(owners, "dashboard.inactive", title, "Nobody has viewed it recently. It stays live, but it is flagged for review.", $"/dashboards/{d.Id}");
            await email.QueueAsync(owners, "dashboard.inactive", title, "Nobody is using this dashboard",
                $"<p><strong>{EmailOutboxService.Encode(d.Name)}</strong> has not been opened by anyone for <strong>{quiet} days</strong> (the limit is {days}).</p>"
                + "<p>It stays live and nothing has been changed. If it is no longer needed, ask a Super Admin to retire it; if it is still needed, make sure the people who should use it know it is there. The flag clears as soon as someone opens it.</p>",
                null, null, ct);
            emailed += owners.Count;
        }
        await db.SaveChangesAsync(ct);

        var text = flagged == 0 ? $"No dashboard has been quiet for {days} days or more."
            : $"Flagged {JobRules.Plural(flagged, "dashboard")} not opened for {days} days or more; {JobRules.Plural(emailed, "owner")} emailed"
              + (withoutOwner > 0 ? $", {JobRules.Plural(withoutOwner, "dashboard")} with no owner to tell." : ".");
        return new JobOutcome(text);
    }
}
