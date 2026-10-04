using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Jobs;

/// <summary>
/// Emails the people hired recently (from the HRMS table) to the Super Admins and anyone holding a role called BPI, so they can be added
/// to the portal and its groups. Weekly looks back 7 days and Monthly 31; Never switches the job off. Nothing is sent when there are no new hires.
/// </summary>
public sealed class NewHireDigestJob(AppDbContext db, EmailOutboxService email, TimeProvider clock) : IScheduledJob
{
    public const string JobName = "new-hire-digest";
    public const string BpiRoleName = "BPI";
    public const int MaxListed = 100;

    public string Name => JobName;
    public string Title => "New-Hire Digest";
    public string Description => "Emails the new-hire list from HRMS to Super Admins and BPI at the chosen frequency (Weekly, Monthly or Never).";

    public async Task<string?> GetScheduleAsync(CancellationToken ct) => JobRules.DigestCron(await JobSettings.GetAsync(db, SettingKeys.NewHireDigestFrequency, ct));

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        var frequency = await JobSettings.GetAsync(db, SettingKeys.NewHireDigestFrequency, ct);
        if (JobRules.DigestCron(frequency) is null) return new JobOutcome("The new-hire digest is switched off (frequency is Never), so nothing was sent.");

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = JobRules.NewHireWindowStart(frequency, today);
        var hires = await db.HrmsSource.AsNoTracking().Where(h => h.HireDate >= from && h.HireDate <= today && h.EmploymentStatus == "Active")
            .OrderByDescending(h => h.HireDate).ThenBy(h => h.LastName).ToListAsync(ct);
        if (hires.Count == 0) return new JobOutcome($"No one was hired since {from:d MMM yyyy}, so no email was sent.");

        var emails = hires.Select(h => h.Email.ToLower()).ToList();
        var inPortal = (await db.Users.AsNoTracking().Where(u => emails.Contains(u.Email.ToLower())).Select(u => u.Email.ToLower()).ToListAsync(ct)).ToHashSet();

        var recipients = await db.UserRoles.Where(r => (r.Role.Name == SystemRoles.SuperAdmin || r.Role.Name == BpiRoleName) && r.User.Status == UserStatus.Active)
            .Select(r => r.UserId).Distinct().ToListAsync(ct);
        if (recipients.Count == 0) return new JobOutcome($"{JobRules.Plural(hires.Count, "new hire")} found but there is no active Super Admin or BPI user to send to.");

        var enc = EmailOutboxService.Encode;
        var rows = string.Join("", hires.Take(MaxListed).Select(h =>
            $"<tr><td style=\"padding:4px 8px 4px 0\">{enc(h.DisplayName ?? $"{h.FirstName} {h.LastName}")}<br><span style=\"color:#5a6877;font-size:12px\">{enc(h.Email)}</span></td>"
            + $"<td style=\"padding:4px 8px\">{enc(h.Department)}{(string.IsNullOrEmpty(h.JobTitle) ? "" : "<br><span style=\"color:#5a6877;font-size:12px\">" + enc(h.JobTitle) + "</span>")}</td>"
            + $"<td style=\"padding:4px 8px\">{h.HireDate:d MMM yyyy}</td><td style=\"padding:4px 0 4px 8px\">{(inPortal.Contains(h.Email.ToLower()) ? "Yes" : "Not yet")}</td></tr>"));
        var more = hires.Count > MaxListed ? $"<p>…and {hires.Count - MaxListed} more.</p>" : "";
        var body = $"<p>{JobRules.Plural(hires.Count, "person", "people")} joined since {from:d MMM yyyy}.</p>"
                   + "<table role=\"presentation\" style=\"border-collapse:collapse;font-size:14px\"><tr style=\"text-align:left;color:#5a6877\"><th>Name</th><th style=\"padding:0 8px\">Department</th><th style=\"padding:0 8px\">Hired</th><th style=\"padding-left:8px\">In the portal</th></tr>"
                   + rows + "</table>" + more
                   + "<p>People who are not in the portal yet can be added from Users, and then to the access groups they need.</p>";
        await email.QueueAsync(recipients, "new-hire-digest", $"New hires: {hires.Count} since {from:d MMM}", "New hires", body, "Open Users", $"{email.Options.AdminPortalUrl}/users", ct);
        await db.SaveChangesAsync(ct);
        return new JobOutcome($"Sent the list of {JobRules.Plural(hires.Count, "new hire")} (since {from:d MMM yyyy}) to {JobRules.Plural(recipients.Count, "person", "people")}.");
    }
}
