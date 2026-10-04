using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;

namespace Vantage.Infrastructure.GenAi;

/// <summary>Settings under "GenAi". The files are served from <see cref="BaseUrl"/>, a different origin from both portals.</summary>
public sealed class GenAiOptions
{
    /// <summary>Where the browser loads GenAI dashboards from: http://localhost:8082 locally, the GenAI domain in AWS.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8082";
    /// <summary>Origins allowed to put a GenAI dashboard in a frame (the two portals), space-separated.</summary>
    public string FrameAncestors { get; set; } = "http://localhost:8080 http://localhost:8081";
    /// <summary>How long a signed link to a dashboard works.</summary>
    public int LinkMinutes { get; set; } = 60;
}

/// <summary>A GenAI file ready to be served: its content and the headers that keep it isolated.</summary>
public sealed record GenAiContent(Stream Content, string ContentSecurityPolicy);

/// <summary>
/// GenAI dashboards: a single HTML file made from the approved template. This service checks uploads, keeps the last
/// versions (Modify Dashboard and restore work like Power BI, but are instant because nothing is imported anywhere),
/// and hands members a short-lived signed link to the file on the separate GenAI origin.
/// A page only ever loads through such a link, and only while its dashboard is Active, so retiring a dashboard
/// stops it being served.
/// </summary>
public sealed class GenAiService(
    AppDbContext db, IFileStore files, IDataProtectionProvider protection, IOptions<GenAiOptions> options, AuditWriter audit,
    NotificationService notifications, TimeProvider clock)
{
    private const string TokenPurpose = "Vantage.GenAi.Link.v1";
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private GenAiOptions Options => options.Value;

    // ---------------------------------------------------------------- checking

    public async Task<IReadOnlyList<string>> ApprovedHostsAsync(CancellationToken ct = default) =>
        await db.ApprovedCdns.AsNoTracking().Where(c => c.IsActive).Select(c => c.Host).ToListAsync(ct);

    /// <summary>Reads the upload (never more than the limit plus one byte) and runs the checks; throws a RuleException listing what's wrong.</summary>
    public async Task<(byte[] Bytes, GenAiCheckResult Result)> ValidateUploadAsync(Stream upload, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".html", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("Choose a single .html file.");
        await ServiceTypes.EnsureAllowedAsync(db, DashboardType.GenAi, upload.CanSeek ? upload.Length : null, ct);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await upload.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Rules.GenAiMaxBytes) break;
        }
        var bytes = buffer.ToArray();
        var result = GenAiChecker.Check(bytes, await ApprovedHostsAsync(ct));
        if (!result.Passed) throw new RuleException("This file can't be published. " + string.Join(" ", result.Errors.Select((e, i) => $"({i + 1}) {e}")));
        return (bytes, result);
    }

    // ---------------------------------------------------------------- versions

    /// <summary>Stores the bytes as the next version of the dashboard and makes it the live one. Trims to the last few versions.</summary>
    public async Task<int> AddVersionAsync(Dashboard d, byte[] html, string fileName, int actorUserId, string? note, CancellationToken ct = default)
    {
        var number = (await db.DashboardVersions.Where(v => v.DashboardId == d.Id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
        var name = Path.GetFileName(fileName);
        var key = $"dashboards/{d.Id}/v{number}/{name}";
        await using var stream = new MemoryStream(html);
        var (size, sha) = await files.SaveAsync(key, stream, ct);

        await db.DashboardVersions.Where(v => v.DashboardId == d.Id && v.IsCurrent).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsCurrent, false), ct);
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = d.Id, VersionNumber = number, FileKey = key, FileName = name, SizeBytes = size, Sha256 = sha,
            IsCurrent = true, ScanStatus = ScanStatus.NotRequired, ScanReport = note, UploadedAtUtc = Now, UploadedByUserId = actorUserId,
        });
        await db.SaveChangesAsync(ct);

        var all = await db.DashboardVersions.Where(v => v.DashboardId == d.Id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        foreach (var old in all.Skip(Rules.VersionsKept).Where(v => !v.IsCurrent))
        {
            files.Delete(old.FileKey);
            db.DashboardVersions.Remove(old);
            audit.Add("dashboard.version-removed", "Dashboard", d.Id, d.Id, new { version = old.VersionNumber, reason = $"Only the last {Rules.VersionsKept} are kept" });
        }
        return number;
    }

    /// <summary>Modify Dashboard for a GenAI dashboard: the new file goes live at once.</summary>
    public async Task<ReplaceStatus> ReplaceAsync(int dashboardId, Stream upload, string fileName, int actorUserId, CancellationToken ct = default)
    {
        var d = await LoadAsync(dashboardId, ct);
        var (bytes, result) = await ValidateUploadAsync(upload, fileName, ct);
        var number = await AddVersionAsync(d, bytes, fileName, actorUserId, null, ct);
        return await FinishAsync(d, number, "dashboard.replaced", new { version = number, type = "GenAi", file = Path.GetFileName(fileName), size = bytes.Length }, result, ct);
    }

    /// <summary>Restores a kept version as a new live version. It's checked again, because the approved CDN list may have changed since.</summary>
    public async Task<ReplaceStatus> RestoreAsync(int dashboardId, int versionNumber, int actorUserId, CancellationToken ct = default)
    {
        var d = await LoadAsync(dashboardId, ct);
        var source = await db.DashboardVersions.AsNoTracking().SingleOrDefaultAsync(v => v.DashboardId == dashboardId && v.VersionNumber == versionNumber, ct)
            ?? throw new KeyNotFoundException();
        if (source.IsCurrent) throw new RuleException($"Version {versionNumber} is already the live version.");

        byte[] bytes;
        await using (var stream = files.OpenRead(source.FileKey))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        var result = GenAiChecker.Check(bytes, await ApprovedHostsAsync(ct));
        if (!result.Passed) throw new RuleException($"Version {versionNumber} no longer passes the checks, so it can't be restored. " + string.Join(" ", result.Errors));
        var number = await AddVersionAsync(d, bytes, source.FileName, actorUserId, $"Restored from version {versionNumber}", ct);
        return await FinishAsync(d, number, "dashboard.restored", new { fromVersion = versionNumber, version = number, type = "GenAi" }, result, ct);
    }

    private async Task<ReplaceStatus> FinishAsync(Dashboard d, int number, string action, object details, GenAiCheckResult result, CancellationToken ct)
    {
        d.UpdatedAtUtc = Now;
        d.LastError = null;
        audit.Add(action, "Dashboard", d.Id, d.Id, details);
        notifications.Notify(new[] { d.PrimaryOwnerId, d.BackupOwnerId }.OfType<int>(), "dashboard.replaced", $"{d.Name} was updated",
            $"Version {number} is live.", $"/dashboards/{d.Id}");
        await db.SaveChangesAsync(ct);
        var message = $"Version {number} is live." + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "");
        return new ReplaceStatus(d.Id, "Succeeded", number, message);
    }

    private async Task<Dashboard> LoadAsync(int dashboardId, CancellationToken ct)
    {
        var d = await db.Dashboards.SingleOrDefaultAsync(x => x.Id == dashboardId, ct) ?? throw new KeyNotFoundException();
        if (d.Type != DashboardType.GenAi) throw new RuleException("This isn't a GenAI dashboard.");
        if (d.Status is not (DashboardStatus.Active or DashboardStatus.Inactive))
            throw new RuleException($"A {d.Status.ToString().ToLowerInvariant()} dashboard can't be modified.");
        return d;
    }

    // ---------------------------------------------------------------- serving

    /// <summary>
    /// A signed link the browser loads in the sandboxed frame. The caller has already decided the person may open the
    /// dashboard (membership for members, Super Admin for a preview). The link expires; the file is never public.
    /// </summary>
    public async Task<(string Url, DateTimeOffset ExpiresAt)> LinkAsync(Dashboard d, CancellationToken ct = default)
    {
        if (!await db.DashboardVersions.AnyAsync(v => v.DashboardId == d.Id && v.IsCurrent, ct))
            throw new Embedding.EmbedException("not-linked", "This GenAI dashboard has no file yet.");
        var expires = clock.GetUtcNow().AddMinutes(Options.LinkMinutes);
        var token = Protector.Protect(d.Id.ToString(), expires);
        return ($"{Options.BaseUrl.TrimEnd('/')}/{token}", expires);
    }

    /// <summary>Opens the live file behind a link, or null when the link is invalid or expired, or the dashboard isn't Active.</summary>
    public async Task<GenAiContent?> OpenAsync(string token, CancellationToken ct = default)
    {
        int dashboardId;
        try { dashboardId = int.Parse(Protector.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }

        var version = await db.DashboardVersions.AsNoTracking()
            .Where(v => v.DashboardId == dashboardId && v.IsCurrent && v.Dashboard.Type == DashboardType.GenAi && v.Dashboard.Status == DashboardStatus.Active)
            .Select(v => v.FileKey).SingleOrDefaultAsync(ct);
        if (version is null) return null;
        try { return new GenAiContent(files.OpenRead(version), PolicyFor(await ApprovedHostsAsync(ct), Options.FrameAncestors)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>
    /// The policy sent with every GenAI page: scripts, styles, fonts and images only from the page itself or approved
    /// CDNs; no network calls, forms, plugins or nested frames; only the portals may frame it; and the page runs in a
    /// sandbox with scripts but no same-origin rights, even if someone opens the link directly.
    /// </summary>
    public static string PolicyFor(IEnumerable<string> approvedHosts, string frameAncestors)
    {
        var cdns = string.Join(' ', approvedHosts.Select(h => "https://" + h));
        return string.Join("; ",
            "default-src 'none'",
            $"script-src 'unsafe-inline' {cdns}".Trim(),
            $"style-src 'unsafe-inline' {cdns}".Trim(),
            $"img-src data: blob: {cdns}".Trim(),
            $"font-src data: {cdns}".Trim(),
            "media-src data: blob:",
            "connect-src 'none'",
            "form-action 'none'",
            "base-uri 'none'",
            "object-src 'none'",
            "frame-src 'none'",
            $"frame-ancestors {(string.IsNullOrWhiteSpace(frameAncestors) ? "'none'" : frameAncestors.Trim())}",
            "sandbox allow-scripts");
    }

    private ITimeLimitedDataProtector Protector => protection.CreateProtector(TokenPurpose).ToTimeLimitedDataProtector();
}
