using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Settings;
using Vantage.Infrastructure.Storage;

namespace Vantage.Infrastructure.Services;

public sealed record SettingView(
    string Key, string Label, string Group, string Kind, string Description, string Value, string DefaultValue,
    int? Min, int? Max, string[]? Options, bool InUse, string? Note, DateTime? UpdatedAtUtc, string? UpdatedBy);

public sealed record CdnView(int Id, string Host, string? Notes, bool IsActive);

public sealed record ServiceTypeView(string Type, string DisplayName, bool IsEnabled, bool RequiresFile, string? AllowedExtensions, int? MaxFileSizeMb);

/// <summary>What the browser needs after sign-in: values that change how the portals behave.</summary>
public sealed record UiSettings(int IdleTimeoutMinutes, int GridPageSize);

/// <summary>
/// Admin Configuration: branding, settings, approved CDNs and the dashboard types. Every value is checked before it is
/// saved and every change is written to the audit log with the old and new value.
/// </summary>
public sealed class ConfigService(AppDbContext db, AuditWriter audit, IFileStore files, TimeProvider clock)
{
    public const int LogoMaxBytes = 512 * 1024;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record Def(string Key, string Label, string Group, string Kind, int? Min = null, int? Max = null, string[]? Options = null, bool InUse = true, string? Note = null);

    private const string ScheduledNote = "Saved now. It takes effect when the scheduled jobs (HRMS sync, inactivity check, new-hire digest) are added.";

    private static readonly Def[] Definitions =
    [
        new(SettingKeys.BrandPortalName, "Portal name", "Branding", "text", 1, 60),
        new(SettingKeys.BrandPrimaryColor, "Primary colour", "Branding", "color"),
        new(SettingKeys.BrandAccentColor, "Accent colour", "Branding", "color"),

        new(SettingKeys.IdleTimeoutMinutes, "Idle timeout (minutes)", "Sign-in and sessions", "int", 5, 480),
        new(SettingKeys.InternalEmailDomain, "Internal email domain", "Sign-in and sessions", "domain"),

        new(SettingKeys.ExternalSeeInternalCatalogue, "External users see Internal dashboards in the catalogue", "Catalogue and screens", "bool"),
        new(SettingKeys.DefaultGridPageSize, "Default rows per page", "Catalogue and screens", "choice", Options: ["10", "25", "50", "100"]),

        new(SettingKeys.EmailEnabled, "Send emails", "Email", "bool"),

        new(SettingKeys.InactivityDays, "Days without views before a dashboard is flagged", "Scheduled jobs", "int", 7, 730, InUse: false, Note: ScheduledNote),
        new(SettingKeys.HrmsSyncCron, "HRMS sync schedule (cron, UTC)", "Scheduled jobs", "cron", InUse: false, Note: ScheduledNote),
        new(SettingKeys.LeaverRevokeImmediately, "Leavers lose access as soon as the HRMS sync marks them Inactive", "Scheduled jobs", "bool", InUse: false, Note: ScheduledNote),
        new(SettingKeys.NewHireDigestFrequency, "New-hire digest", "Scheduled jobs", "choice", Options: ["Weekly", "Monthly", "Never"], InUse: false, Note: ScheduledNote),

        // GenAI Config tab. Group names start with "GenAI" (the Settings tab hides them; the GenAI Config tab shows them).
        new(SettingKeys.GenAiBaseUrl, "GenAI web address", GenAiGroupServing, "url"),
        new(SettingKeys.GenAiFrameAncestors, "Portals allowed to show GenAI dashboards", GenAiGroupServing, "origins"),
        new(SettingKeys.GenAiLinkMinutes, "Link lifetime (minutes)", GenAiGroupServing, "int", 1, 1440),
        new(SettingKeys.GenAiWarnSizeMb, "Warn when a file is this big (MB)", GenAiGroupChecks, "int", 1, 5),
        new(SettingKeys.GenAiScanHost, "Malware scanner host", GenAiGroupScan, "host"),
        new(SettingKeys.GenAiScanPort, "Malware scanner port", GenAiGroupScan, "int", 1, 65535),
    ];

    public const string GenAiGroupServing = "GenAI Serving";
    public const string GenAiGroupChecks = "GenAI Upload Checks";
    public const string GenAiGroupScan = "GenAI Malware Scan";

    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static readonly Regex Domain = new("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*\\.[a-z]{2,}$", RegexOptions.Compiled);
    private static readonly Regex HostName = new("^[A-Za-z0-9]([A-Za-z0-9.\\-]*[A-Za-z0-9])?$", RegexOptions.Compiled);
    private static readonly Regex CronField = new("^[0-9*/,\\-]+$", RegexOptions.Compiled);

    // ------------------------------------------------------------ Settings

    public async Task<List<SettingView>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, ct);
        var editorIds = rows.Values.Where(r => r.UpdatedByUserId != null).Select(r => r.UpdatedByUserId!.Value).Distinct().ToList();
        var editors = await db.Users.AsNoTracking().Where(u => editorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email, ct);
        return Definitions.Select(d =>
        {
            var def = SettingKeys.Defaults.First(x => x.Key == d.Key);
            rows.TryGetValue(d.Key, out var row);
            return new SettingView(d.Key, d.Label, d.Group, d.Kind, def.Description, row?.Value ?? def.Value, def.Value, d.Min, d.Max, d.Options, d.InUse, d.Note,
                row?.UpdatedAtUtc, row?.UpdatedByUserId is { } id && editors.TryGetValue(id, out var name) ? name : null);
        }).ToList();
    }

    /// <summary>Checks every value first, then saves the ones that changed. Nothing is saved if any value is wrong.</summary>
    public async Task<int> UpdateAsync(IReadOnlyDictionary<string, string?> values, int actorId, CancellationToken ct = default)
    {
        var current = await db.SystemSettings.ToDictionaryAsync(s => s.Key, ct);
        var changes = new List<(Def Def, string From, string To)>();
        foreach (var (key, raw) in values)
        {
            var def = Definitions.FirstOrDefault(d => d.Key == key) ?? throw new RuleException($"'{key}' is not a setting you can change here.");
            var value = Normalise(def, raw);
            var from = current.TryGetValue(key, out var row) ? row.Value : SettingKeys.Defaults.First(x => x.Key == key).Value;
            if (from != value) changes.Add((def, from, value));
        }
        foreach (var (def, from, to) in changes)
        {
            await SetAsync(current, def.Key, to, actorId, ct);
            audit.Add("config.setting-changed", "SystemSetting", def.Key, details: new { key = def.Key, label = def.Label, from, to });
        }
        await db.SaveChangesAsync(ct);
        return changes.Count;
    }

    /// <summary>The cleaned value for a setting, or a RuleException saying what is wrong. Used by Save and by tests, which need no database for it.</summary>
    internal static string NormaliseValue(string key, string? raw) =>
        Normalise(Definitions.FirstOrDefault(d => d.Key == key) ?? throw new RuleException($"'{key}' is not a setting you can change here."), raw);

    private static string Normalise(Def d, string? raw)
    {
        var v = (raw ?? "").Trim();
        switch (d.Kind)
        {
            case "text":
                if (v.Length < (d.Min ?? 1) || v.Length > (d.Max ?? 200)) throw new RuleException($"{d.Label} must be {d.Min ?? 1} to {d.Max ?? 200} characters.");
                return v;
            case "int":
                if (!int.TryParse(v, out var n) || n < d.Min || n > d.Max) throw new RuleException($"{d.Label} must be a whole number from {d.Min} to {d.Max}.");
                return n.ToString();
            case "bool":
                if (!bool.TryParse(v, out var b)) throw new RuleException($"{d.Label} must be on or off.");
                return b ? "true" : "false";
            case "choice":
                return d.Options!.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)) ?? throw new RuleException($"{d.Label} must be one of: {string.Join(", ", d.Options!)}.");
            case "color":
                if (!Hex.IsMatch(v)) throw new RuleException($"{d.Label} must be a colour like #1F4E79.");
                return v.ToUpperInvariant();
            case "domain":
                v = v.TrimStart('@').ToLowerInvariant();
                if (!Domain.IsMatch(v)) throw new RuleException($"{d.Label} must be a domain like rrd.com.");
                return v;
            case "host":
                if (v.Length == 0) return "";
                if (v.Length > 253 || !HostName.IsMatch(v)) throw new RuleException($"{d.Label} must be a host name or address like clamav or 10.0.0.5, without http:// or a port, or blank.");
                return v;
            case "url":
                if (v.Length == 0) return "";
                return Origin(v) ?? throw new RuleException($"{d.Label} must be a web address like https://genai.example.com (no path, no login details), or blank.");
            case "origins":
                if (v.Length == 0) return "";
                var origins = v.Split([' ', ',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(o => Origin(o) ?? throw new RuleException($"“{o}” isn't a portal address. Use addresses like https://portal.example.com, separated by spaces.")).Distinct().ToList();
                if (origins.Count > 10) throw new RuleException($"{d.Label} can list up to 10 addresses.");
                return string.Join(' ', origins);
            case "cron":
                var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5 || parts.Any(p => !CronField.IsMatch(p))) throw new RuleException($"{d.Label} must be five fields like 0 2 1 * * (minute, hour, day of month, month, day of week).");
                return string.Join(' ', parts);
            default:
                return v;
        }
    }

    /// <summary>"https://host[:port]" for a plain web address (http or https, no wildcard, login details, path, query or fragment), else null.</summary>
    internal static string? Origin(string value)
    {
        if (value.Contains('*') || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/") return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task SetAsync(Dictionary<string, SystemSetting> current, string key, string value, int actorId, CancellationToken ct)
    {
        if (!current.TryGetValue(key, out var row))
        {
            row = new SystemSetting { Key = key, Description = SettingKeys.Defaults.FirstOrDefault(x => x.Key == key).Description };
            db.SystemSettings.Add(row);
            current[key] = row;
        }
        row.Value = value;
        row.UpdatedAtUtc = Now;
        row.UpdatedByUserId = actorId;
        await Task.CompletedTask;
    }

    /// <summary>The values that change how the portals behave for every signed-in person.</summary>
    public async Task<UiSettings> UiAsync(CancellationToken ct = default)
    {
        var s = await db.SystemSettings.AsNoTracking().Where(x => x.Key == SettingKeys.IdleTimeoutMinutes || x.Key == SettingKeys.DefaultGridPageSize).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return new UiSettings(
            s.TryGetValue(SettingKeys.IdleTimeoutMinutes, out var idle) && int.TryParse(idle, out var i) ? i : 30,
            s.TryGetValue(SettingKeys.DefaultGridPageSize, out var grid) && int.TryParse(grid, out var g) ? g : 25);
    }

    // ------------------------------------------------------------ Logo

    /// <summary>Saves an uploaded logo (PNG, JPG, WebP or SVG, up to 512 KB) and points the branding at it.</summary>
    public async Task SaveLogoAsync(Stream content, string fileName, int actorId, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > LogoMaxBytes) throw new RuleException("The logo can be up to 512 KB.");
        }
        var bytes = buffer.ToArray();
        var ext = SniffImage(bytes) ?? throw new RuleException("The logo must be a PNG, JPG, WebP or SVG image.");

        var token = Guid.NewGuid().ToString("N");
        var key = $"branding/logo-{token}{ext}";
        buffer.Position = 0;
        await files.SaveAsync(key, buffer, ct);

        var current = await db.SystemSettings.ToDictionaryAsync(s => s.Key, ct);
        if (current.TryGetValue(SettingKeys.BrandLogoFile, out var old) && !string.IsNullOrEmpty(old.Value)) files.Delete(old.Value);
        await SetAsync(current, SettingKeys.BrandLogoFile, key, actorId, ct);
        await SetAsync(current, SettingKeys.BrandLogoUrl, $"/api/branding/logo?v={token}", actorId, ct);
        audit.Add("config.logo-changed", "SystemSetting", SettingKeys.BrandLogoUrl, details: new { file = Path.GetFileName(fileName), bytes = bytes.Length });
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetLogoAsync(int actorId, CancellationToken ct = default)
    {
        var current = await db.SystemSettings.ToDictionaryAsync(s => s.Key, ct);
        if (current.TryGetValue(SettingKeys.BrandLogoFile, out var old) && !string.IsNullOrEmpty(old.Value)) files.Delete(old.Value);
        await SetAsync(current, SettingKeys.BrandLogoFile, "", actorId, ct);
        await SetAsync(current, SettingKeys.BrandLogoUrl, SettingKeys.DefaultLogoUrl, actorId, ct);
        audit.Add("config.logo-reset", "SystemSetting", SettingKeys.BrandLogoUrl);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The extension for an image the portal accepts, judged by the file's own bytes (not its name); null otherwise.</summary>
    private static string? SniffImage(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 4096)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.StartsWith("<?xml", StringComparison.Ordinal) || head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            var text = System.Text.Encoding.UTF8.GetString(b);
            if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return null;
            if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, "\\son\\w+\\s*=", RegexOptions.IgnoreCase)) return null; // no scripts in a logo
            return ".svg";
        }
        return null;
    }

    // ------------------------------------------------------------ Approved CDNs

    public async Task<List<CdnView>> CdnsAsync(CancellationToken ct = default) =>
        await db.ApprovedCdns.AsNoTracking().OrderBy(c => c.Host).Select(c => new CdnView(c.Id, c.Host, c.Notes, c.IsActive)).ToListAsync(ct);

    public async Task<CdnView> AddCdnAsync(string? host, string? notes, CancellationToken ct = default)
    {
        var h = NormaliseHost(host);
        if (await db.ApprovedCdns.AnyAsync(c => c.Host == h, ct)) throw new RuleException($"{h} is already on the list.");
        var cdn = new ApprovedCdn { Host = h, Notes = CleanNotes(notes), IsActive = true };
        db.ApprovedCdns.Add(cdn);
        await db.SaveChangesAsync(ct);
        audit.Add("config.cdn-added", "ApprovedCdn", cdn.Id, details: new { host = h });
        await db.SaveChangesAsync(ct);
        return new CdnView(cdn.Id, cdn.Host, cdn.Notes, cdn.IsActive);
    }

    public async Task UpdateCdnAsync(int id, string? notes, bool active, CancellationToken ct = default)
    {
        var cdn = await db.ApprovedCdns.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        cdn.Notes = CleanNotes(notes);
        var activeChanged = cdn.IsActive != active;
        cdn.IsActive = active;
        audit.Add("config.cdn-updated", "ApprovedCdn", cdn.Id, details: new { host = cdn.Host, active, activeChanged });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveCdnAsync(int id, CancellationToken ct = default)
    {
        var cdn = await db.ApprovedCdns.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        audit.Add("config.cdn-removed", "ApprovedCdn", cdn.Id, details: new { host = cdn.Host });
        db.ApprovedCdns.Remove(cdn);
        await db.SaveChangesAsync(ct);
    }

    private static string NormaliseHost(string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Length == 0) throw new RuleException("Enter the CDN's host name, for example cdnjs.cloudflare.com.");
        if (h.Contains("://") || h.Contains('/') || h.Contains(':') || h.Contains(' ') || h.Contains('*'))
            throw new RuleException("Enter only the host name, without https://, a path, a port or wildcards.");
        if (h.Length > 200 || !Domain.IsMatch(h)) throw new RuleException("That doesn't look like a host name, for example cdn.jsdelivr.net.");
        return h;
    }

    private static string? CleanNotes(string? notes)
    {
        var t = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (t?.Length > 200) throw new RuleException("Notes can be up to 200 characters.");
        return t;
    }

    // ------------------------------------------------------------ Dashboard types (BI Service Master)

    public async Task<List<ServiceTypeView>> TypesAsync(CancellationToken ct = default) =>
        await db.BiServiceTypes.AsNoTracking().OrderBy(t => t.Type)
            .Select(t => new ServiceTypeView(t.Type.ToString(), t.DisplayName, t.IsEnabled, t.RequiresFile, t.AllowedExtensions, t.MaxFileSizeMb)).ToListAsync(ct);

    /// <summary>Switches a dashboard type on or off and sets its largest upload. At least one type must stay on.</summary>
    public async Task UpdateTypeAsync(DashboardType type, bool enabled, int? maxFileSizeMb, CancellationToken ct = default)
    {
        var t = await db.BiServiceTypes.SingleOrDefaultAsync(x => x.Type == type, ct) ?? throw new KeyNotFoundException();
        if (!enabled && !await db.BiServiceTypes.AnyAsync(x => x.Type != type && x.IsEnabled, ct))
            throw new RuleException("At least one dashboard type must stay switched on.");
        if (t.RequiresFile)
        {
            if (maxFileSizeMb is not { } mb || mb < 1 || mb > 2048) throw new RuleException("The largest upload must be from 1 to 2048 MB.");
            t.MaxFileSizeMb = mb;
        }
        var wasEnabled = t.IsEnabled;
        t.IsEnabled = enabled;
        audit.Add("config.service-type-updated", "BiServiceType", t.Type.ToString(), details: new { type = t.DisplayName, enabled, wasEnabled, maxFileSizeMb = t.MaxFileSizeMb });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Enforces what Admin Configuration says about a dashboard type (switched on, largest upload).</summary>
public static class ServiceTypes
{
    public static async Task EnsureAllowedAsync(AppDbContext db, DashboardType type, long? sizeBytes, CancellationToken ct = default)
    {
        var t = await db.BiServiceTypes.AsNoTracking().SingleOrDefaultAsync(x => x.Type == type, ct);
        if (t is null) return;
        if (!t.IsEnabled) throw new RuleException($"{t.DisplayName} dashboards are switched off. A Super Admin can switch them on in Admin Configuration.");
        if (t.MaxFileSizeMb is { } mb && sizeBytes is { } size && size > mb * 1024L * 1024L)
            throw new RuleException($"The file is {size / 1024d / 1024d:0.#} MB, over the {mb} MB limit for {t.DisplayName} dashboards.");
    }
}
