using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Email;

/// <summary>Email settings. Locally the API sends to the Mailpit container; in AWS this becomes Amazon SES.</summary>
public sealed class EmailOptions
{
    public bool Enabled { get; set; } = true;
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool UseSsl { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "vantage@rrd.com";
    /// <summary>Links in emails point here: the User Portal and the Admin Portal.</summary>
    public string UserPortalUrl { get; set; } = "http://localhost:8081";
    public string AdminPortalUrl { get; set; } = "http://localhost:8080";
}

/// <summary>
/// Puts emails in the outbox table (in the caller's unit of work, so an email is only sent if the change is saved).
/// <see cref="EmailSenderWorker"/> sends them.
/// </summary>
public sealed class EmailOutboxService(AppDbContext db, IOptions<EmailOptions> options, TimeProvider clock)
{
    public EmailOptions Options => options.Value;

    public async Task QueueAsync(IEnumerable<int> userIds, string template, string subject, string heading, string bodyHtml,
        string? buttonText, string? link, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var emails = await db.Users.Where(u => ids.Contains(u.Id) && u.Status != UserStatus.Inactive).Select(u => u.Email).ToListAsync(ct);
        var portal = await db.SystemSettings.Where(s => s.Key == SettingKeys.BrandPortalName).Select(s => s.Value).SingleOrDefaultAsync(ct) ?? "Vantage";
        foreach (var to in emails)
            db.EmailOutbox.Add(new EmailOutbox
            {
                ToAddress = to, Subject = $"{portal}: {subject}", Template = template, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                HtmlBody = Render(portal, heading, bodyHtml, buttonText, link),
            });
    }

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>A plain, single-column email that reads well in Outlook.</summary>
    private static string Render(string portal, string heading, string bodyHtml, string? buttonText, string? link)
    {
        var button = buttonText is null || link is null ? "" :
            $"""<p style="margin:24px 0"><a href="{Encode(link)}" style="background:#1F4E79;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:4px;font-weight:600;display:inline-block">{Encode(buttonText)}</a></p>""";
        return $"""
            <!doctype html>
            <html><body style="margin:0;padding:24px;background:#f3f5f7;font-family:Segoe UI,Arial,sans-serif;color:#18222d">
            <table role="presentation" width="100%" style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #d8dee5;border-radius:8px">
            <tr><td style="padding:20px 24px;border-bottom:1px solid #d8dee5;font-weight:700;color:#1F4E79">{Encode(portal)}</td></tr>
            <tr><td style="padding:24px">
            <h1 style="font-size:20px;margin:0 0 12px">{Encode(heading)}</h1>
            <div style="font-size:15px;line-height:1.5">{bodyHtml}</div>
            {button}
            <p style="font-size:12px;color:#5a6877;margin-top:24px">This is an automatic message from {Encode(portal)}. Please don't reply.</p>
            </td></tr></table></body></html>
            """;
    }
}

/// <summary>Sends pending outbox emails every few seconds; each email is tried up to 5 times.</summary>
public sealed class EmailSenderWorker(IServiceScopeFactory scopes, IOptions<EmailOptions> options, TimeProvider clock, ILogger<EmailSenderWorker> log) : BackgroundService
{
    public const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Email sending is switched off (Email:Enabled = false); emails stay in the outbox.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendBatchAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Sending outbox emails failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task SendBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Admin Configuration can switch sending off; the emails then wait in the outbox until it is switched on again.
        var sendEmails = await db.SystemSettings.Where(s => s.Key == SettingKeys.EmailEnabled).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (string.Equals(sendEmails, "false", StringComparison.OrdinalIgnoreCase)) return;
        var batch = await db.EmailOutbox.Where(e => e.Status == EmailStatus.Pending && e.Attempts < MaxAttempts)
            .OrderBy(e => e.CreatedAtUtc).Take(20).ToListAsync(ct);
        if (batch.Count == 0) return;

        var o = options.Value;
        using var smtp = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseSsl };
        if (!string.IsNullOrEmpty(o.UserName)) smtp.Credentials = new NetworkCredential(o.UserName, o.Password);
        foreach (var e in batch)
        {
            e.Attempts++;
            try
            {
                using var message = new MailMessage(o.FromAddress, e.ToAddress, e.Subject, e.HtmlBody) { IsBodyHtml = true };
                await smtp.SendMailAsync(message, ct);
                e.Status = EmailStatus.Sent;
                e.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                e.LastError = null;
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
            {
                e.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                if (e.Attempts >= MaxAttempts) e.Status = EmailStatus.Failed;
                log.LogWarning("Email {Id} to {To} failed (attempt {Attempt}): {Error}", e.Id, e.ToAddress, e.Attempts, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
