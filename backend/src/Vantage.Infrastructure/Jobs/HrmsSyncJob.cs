using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Jobs;

/// <summary>What the HRMS job did, for the caller of Run Now and the Users page.</summary>
public sealed record HrmsJobResult(HrmsSyncSummary Sync, int LeaversRevoked, int MembershipsEnded, List<RuleRunResult> Rules);

/// <summary>
/// The HRMS job (monthly by default): refreshes profiles and marks leavers Inactive (<see cref="HrmsSyncService"/>), then,
/// when "Leavers lose access" is on, ends the leavers' group memberships, then runs the access group rules so the owners hear about
/// anyone the fresh data puts inside a rule. It never creates users (people are added in Users; U5 is still open).
/// Inactive already stops sign-in at once, so there is no separate session to end.
/// </summary>
public sealed class HrmsSyncJob(AppDbContext db, HrmsSyncService sync, AccessGroupRuleService rules, AuditWriter audit, NotificationService notifications,
    TimeProvider clock, ILogger<HrmsSyncJob> log) : IScheduledJob
{
    public const string JobName = "hrms-sync";
    public string Name => JobName;
    public string Title => "HRMS Sync";
    public string Description => "Reads the HRMS table, refreshes user profiles, marks leavers Inactive, ends their access when that setting is on, then runs the access group rules.";

    public async Task<string?> GetScheduleAsync(CancellationToken ct) => await JobSettings.GetAsync(db, SettingKeys.HrmsSyncCron, ct);

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        var summary = await sync.RunAsync(ct);

        int revoked = 0, ended = 0;
        if (await JobSettings.GetBoolAsync(db, SettingKeys.LeaverRevokeImmediately, ct))
            (revoked, ended) = await RevokeLeaversAsync(summary.DeactivatedEmails, ct);

        // Fresh HRMS data may put new people inside a rule. This sends proposals to the owners; nothing changes until they approve.
        List<RuleRunResult> ruleResults = [];
        string rulesText;
        try
        {
            ruleResults = await rules.RunAllAsync(ct);
            var sent = ruleResults.Sum(r => r.Proposed);
            rulesText = ruleResults.Count == 0 ? "No access group rules." : $"Access group rules: {JobRules.Plural(ruleResults.Count, "rule")} run, {JobRules.Plural(sent, "person", "people")} sent to owners.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Access group rules failed after the HRMS sync");
            db.ChangeTracker.Clear(); // drop whatever the failed rule run left half-done
            rulesText = "Access group rules failed to run (see the log).";
        }

        var text = $"Checked {summary.Checked}: {summary.ProfilesUpdated} updated, {summary.Deactivated} marked Inactive"
                   + (summary.SkippedManual > 0 ? $" ({summary.SkippedManual} left alone: status set by hand)" : "")
                   + (summary.NotInHrms > 0 ? $", {summary.NotInHrms} not found in HRMS" : "") + ". "
                   + (revoked > 0 ? $"Access ended for {JobRules.Plural(revoked, "leaver")} ({JobRules.Plural(ended, "group membership")}). " : "")
                   + rulesText;
        return new JobOutcome(text, new HrmsJobResult(summary, revoked, ended, ruleResults));
    }

    /// <summary>
    /// Ends every live group membership of people HRMS says have left. People who own a dashboard keep that membership (an owner can't
    /// be removed, so a change of owner in Dashboards Master comes first); Super Admins are told about the ones who just left.
    /// </summary>
    private async Task<(int Leavers, int Memberships)> RevokeLeaversAsync(IReadOnlyList<string> justDeactivated, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var leavers = await db.Users
            .Where(u => u.UserType == UserType.Internal && u.Status == UserStatus.Inactive && u.HrmsProfile != null && u.HrmsProfile.EmploymentStatus != "Active"
                        && db.GroupMembers.Any(m => m.UserId == u.Id && m.RemovedAtUtc == null))
            .Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        if (leavers.Count == 0) return (0, 0);

        var ids = leavers.Select(l => l.Id).ToList();
        var memberships = await db.GroupMembers.Include(m => m.Group).ThenInclude(g => g.Dashboard)
            .Where(m => ids.Contains(m.UserId) && m.RemovedAtUtc == null).ToListAsync(ct);

        int people = 0, total = 0;
        var ownerLeavers = new List<string>();
        foreach (var leaver in leavers)
        {
            var mine = memberships.Where(m => m.UserId == leaver.Id).ToList();
            var ending = mine.Where(m => m.Group.Dashboard.PrimaryOwnerId != leaver.Id && m.Group.Dashboard.BackupOwnerId != leaver.Id).ToList();
            var owned = mine.Count - ending.Count;
            foreach (var m in ending)
            {
                m.RemovedAtUtc = now;
                m.RemovedByUserId = null;
                m.RemovedReason = "Left the company (HRMS)";
            }
            if (ending.Count > 0) { people++; total += ending.Count; }
            if (owned > 0 && justDeactivated.Contains(leaver.Email, StringComparer.OrdinalIgnoreCase)) ownerLeavers.Add(leaver.Email);
            if (ending.Count > 0 || owned > 0)
                audit.Add("user.access-revoked", "User", leaver.Id, details: new { email = leaver.Email, memberships = ending.Count, keptAsOwner = owned });
        }

        if (ownerLeavers.Count > 0)
        {
            var admins = await db.UserRoles.Where(r => r.Role.Name == SystemRoles.SuperAdmin && r.User.Status == UserStatus.Active).Select(r => r.UserId).ToListAsync(ct);
            notifications.Notify(admins, "hrms.leaver-owns-dashboards", "A leaver still owns dashboards",
                $"{string.Join(", ", ownerLeavers)} left the company but {(ownerLeavers.Count == 1 ? "is" : "are")} still named as an owner. Change the owner in Dashboards Master.", "/dashboards");
        }
        await db.SaveChangesAsync(ct);
        return (people, total);
    }
}
