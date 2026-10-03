using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

public sealed record HrmsSyncSummary(int Checked, int ProfilesUpdated, int Deactivated, int SkippedManual, int NotInHrms, IReadOnlyList<string> DeactivatedEmails);

/// <summary>
/// The HRMS job: reads the hrms.EmployeeProfile table (filled by the SQL-level sync) and, for each internal user
/// matched by email, refreshes their HRMS profile and marks leavers Inactive, unless an admin set the status by hand.
/// It never creates users and never reactivates anyone (re-employment is reactivated by an admin).
/// Scheduled monthly; Super Admins can also run it from the User Master.
/// </summary>
public sealed class HrmsSyncService(AppDbContext db, AuditWriter audit, TimeProvider clock)
{
    public async Task<HrmsSyncSummary> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new JobRun { JobName = "hrms-sync", StartedAtUtc = now, Status = JobRunStatus.Running };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var source = await db.HrmsSource.AsNoTracking().ToDictionaryAsync(h => h.Email.ToLower(), ct);
        var users = await db.Users.Include(u => u.HrmsProfile).Where(u => u.UserType == UserType.Internal).ToListAsync(ct);

        int updated = 0, deactivated = 0, skipped = 0, missing = 0;
        var leavers = new List<string>();
        foreach (var user in users)
        {
            if (!source.TryGetValue(user.Email.ToLower(), out var h)) { missing++; continue; }

            if (user.HrmsProfile is null) user.HrmsProfile = ProfileFrom(h, now);
            else Copy(h, user.HrmsProfile, now);
            user.FirstName ??= h.FirstName;
            user.LastName ??= h.LastName;
            user.DisplayName ??= h.DisplayName;
            updated++;

            if (h.EmploymentStatus != "Active" && user.Status != UserStatus.Inactive)
            {
                if (user.StatusSetManually) { skipped++; continue; }
                user.Status = UserStatus.Inactive;
                user.UpdatedAtUtc = now;
                deactivated++;
                leavers.Add(user.Email);
                audit.Add("user.deactivated-by-hrms", "User", user.Id, details: new { h.ExitDate });
            }
        }

        var summary = new HrmsSyncSummary(users.Count, updated, deactivated, skipped, missing, leavers);
        run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        run.Status = JobRunStatus.Succeeded;
        run.Summary = System.Text.Json.JsonSerializer.Serialize(summary);
        audit.Add("hrms.sync", "JobRun", run.Id, details: summary);
        await db.SaveChangesAsync(ct);
        return summary;
    }

    public static HrmsProfile ProfileFrom(HrmsEmployeeSource h, DateTime now)
    {
        var p = new HrmsProfile();
        Copy(h, p, now);
        return p;
    }

    private static void Copy(HrmsEmployeeSource h, HrmsProfile p, DateTime now)
    {
        p.EmployeeId = h.EmployeeId;
        p.Department = h.Department;
        p.Division = h.Division;
        p.JobTitle = h.JobTitle;
        p.JobGrade = h.JobGrade;
        p.ManagerEmail = h.ManagerEmail;
        p.Location = h.Location;
        p.EmploymentStatus = h.EmploymentStatus;
        p.HireDate = h.HireDate;
        p.ExitDate = h.ExitDate;
        p.LastSyncedAtUtc = now;
    }

    public static string? TimeZoneFor(string? location) => location switch
    {
        "Chicago" => "America/Chicago",
        "Monterrey" => "America/Monterrey",
        "Chennai" => "Asia/Kolkata",
        "Colombo" => "Asia/Colombo",
        _ => null,
    };
}
