using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Settings;

namespace Vantage.Infrastructure.GenAi;

/// <summary>The GenAI values in force right now.</summary>
public sealed record GenAiRuntime(string BaseUrl, string FrameAncestors, int LinkMinutes, int WarnBytes, string? ScanHost, int ScanPort);

/// <summary>
/// Reads the GenAI settings from Admin Configuration (the GenAI Config tab) every time they are needed, so a change
/// applies at once with no restart. A blank address or scanner host falls back to the environment's deployment setting
/// (<see cref="GenAiOptions"/>, the "GenAi" section of the configuration); the numbers always come from Admin Configuration.
/// </summary>
public sealed class GenAiSettings(AppDbContext db, IOptions<GenAiOptions> deployed)
{
    public async Task<GenAiRuntime> GetAsync(CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.AsNoTracking().Where(s => s.Key.StartsWith(SettingKeys.GenAiPrefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string? Text(string key) => rows.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int Number(string key, int fallback) => int.TryParse(Text(key), out var n) ? n : fallback;

        var d = deployed.Value;
        return new GenAiRuntime(
            BaseUrl: Text(SettingKeys.GenAiBaseUrl) ?? d.BaseUrl,
            FrameAncestors: Text(SettingKeys.GenAiFrameAncestors) ?? d.FrameAncestors,
            LinkMinutes: Number(SettingKeys.GenAiLinkMinutes, d.LinkMinutes),
            WarnBytes: Number(SettingKeys.GenAiWarnSizeMb, 2) * 1024 * 1024,
            ScanHost: Text(SettingKeys.GenAiScanHost) ?? (string.IsNullOrWhiteSpace(d.ScanHost) ? null : d.ScanHost.Trim()),
            ScanPort: Number(SettingKeys.GenAiScanPort, d.ScanPort));
    }
}

/// <summary>Chooses the ClamAV scanner or none each time, from the current settings, so switching the scanner on or off needs no restart.</summary>
public sealed class ConfiguredFileScanner(GenAiSettings settings) : IFileScanner
{
    public async Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        IFileScanner scanner = s.ScanHost is { Length: > 0 } host ? new ClamAvScanner(host, s.ScanPort) : new NoFileScanner();
        return await scanner.ScanAsync(content, ct);
    }
}
