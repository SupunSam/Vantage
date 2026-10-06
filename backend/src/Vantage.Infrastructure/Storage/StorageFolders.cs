using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Storage;

/// <summary>
/// Where uploaded dashboard files go (C61): a sub-folder under the storage root for each BI type, set in Admin Configuration,
/// and a timestamp in the file name. The same value will be the key prefix once the S3 store is added.
/// </summary>
public static class StorageFolders
{
    public const int MaxLength = 100;

    /// <summary>A tidy folder path: letters, digits, dash, underscore, dot and "/" between parts. No drive letters, no "..", never absolute.</summary>
    public static string Clean(string? raw, string label = "The folder")
    {
        var v = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
        if (v.Length == 0 || v.Length > MaxLength) throw new RuleException($"{label} must be 1 to {MaxLength} characters.");
        foreach (var part in v.Split('/'))
        {
            if (part.Length == 0 || part is "." or "..") throw new RuleException($"{label} can't contain empty parts or '..'.");
            if (!part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new RuleException($"{label} can use letters, digits, '-', '_' and '.', with '/' between folders.");
        }
        return v;
    }

    /// <summary>The configured folder for a type's uploads; Tableau has no file, so it uses a shared default.</summary>
    public static async Task<string> ForAsync(AppDbContext db, BiType type, CancellationToken ct = default)
    {
        var (key, fallback) = type switch
        {
            BiType.GenAi => (SettingKeys.StorageGenAiFolder, "genai"),
            _ => (SettingKeys.StoragePowerBiFolder, "powerbi"),
        };
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
        try { return Clean(string.IsNullOrWhiteSpace(value) ? fallback : value); } catch (RuleException) { return fallback; }
    }

    /// <summary>`folder/dashboardId/vN/name_yyyyMMdd-HHmmss.ext`; the stored FileName stays the original name.</summary>
    public static string Key(string folder, int dashboardId, int version, string fileName, DateTime utcNow)
    {
        var name = Path.GetFileName(fileName);
        return $"{folder}/{dashboardId}/v{version}/{Path.GetFileNameWithoutExtension(name)}_{utcNow:yyyyMMdd-HHmmss}{Path.GetExtension(name)}";
    }
}
