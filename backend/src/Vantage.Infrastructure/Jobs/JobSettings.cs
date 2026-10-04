using Microsoft.EntityFrameworkCore;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.Jobs;

/// <summary>Reads a setting with the seeded default as the fallback, so a job never runs on a missing value.</summary>
internal static class JobSettings
{
    public static async Task<string> GetAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(value) ? SettingKeys.Defaults.First(d => d.Key == key).Value : value;
    }

    public static async Task<int> GetIntAsync(AppDbContext db, string key, CancellationToken ct) =>
        int.TryParse(await GetAsync(db, key, ct), out var n) ? n : int.Parse(SettingKeys.Defaults.First(d => d.Key == key).Value);

    public static async Task<bool> GetBoolAsync(AppDbContext db, string key, CancellationToken ct) =>
        string.Equals(await GetAsync(db, key, ct), "true", StringComparison.OrdinalIgnoreCase);
}
