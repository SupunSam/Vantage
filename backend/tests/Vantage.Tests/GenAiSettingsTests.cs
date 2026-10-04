using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.GenAi;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>The GenAI Config tab: validated, audited settings that apply at once, with the environment as the fallback.</summary>
public class GenAiSettingsTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private sealed class NoFiles : IFileStore
    {
        public Task<(long Size, string Sha256)> SaveAsync(string key, Stream content, CancellationToken ct = default) => Task.FromResult((0L, ""));
        public Stream OpenRead(string key) => new MemoryStream();
        public void Delete(string key) { }
    }

    private sealed record Kit(AppDbContext Db, ConfigService Config, User Admin) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var admin = new User { Email = $"{Guid.NewGuid():N}@rrd.com", DisplayName = "Admin", UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        return new Kit(db, new ConfigService(db, new AuditWriter(db, new Ctx(), TimeProvider.System), new NoFiles(), TimeProvider.System), admin);
    }

    private static Task Save(Kit k, string key, string? value) => k.Config.UpdateAsync(new Dictionary<string, string?> { [key] = value }, k.Admin.Id);

    /// <summary>Puts every GenAI setting back to its default so tests don't affect each other.</summary>
    private static async Task ResetAsync(Kit k)
    {
        foreach (var (key, value, _) in SettingKeys.Defaults.Where(d => d.Key.StartsWith(SettingKeys.GenAiPrefix)))
            await k.Db.SystemSettings.Where(s => s.Key == key).ExecuteUpdateAsync(u => u.SetProperty(s => s.Value, value));
    }

    [SqlFact]
    public async Task The_genai_settings_are_listed_in_their_own_groups_with_defaults()
    {
        await using var k = await ArrangeAsync();

        var genAi = (await k.Config.ListAsync()).Where(s => s.Key.StartsWith(SettingKeys.GenAiPrefix)).ToList();

        Assert.Equal(6, genAi.Count);
        Assert.All(genAi, s => Assert.StartsWith("GenAI", s.Group));
        Assert.Equal("60", genAi.Single(s => s.Key == SettingKeys.GenAiLinkMinutes).Value);
        Assert.Equal("3310", genAi.Single(s => s.Key == SettingKeys.GenAiScanPort).Value);
        Assert.Equal("", genAi.Single(s => s.Key == SettingKeys.GenAiScanHost).Value);
    }

    [SqlFact]
    public async Task Good_values_are_saved_cleaned_up_and_audited()
    {
        await using var k = await ArrangeAsync();
        try
        {
            var before = await k.Db.AuditLogs.CountAsync(a => a.Action == "config.setting-changed");

            var changed = await k.Config.UpdateAsync(new Dictionary<string, string?>
            {
                [SettingKeys.GenAiBaseUrl] = "HTTPS://GenAI.Example.com/",
                [SettingKeys.GenAiFrameAncestors] = "https://admin.example.com, https://portal.example.com:8443 https://admin.example.com",
                [SettingKeys.GenAiLinkMinutes] = "15",
                [SettingKeys.GenAiWarnSizeMb] = "1",
                [SettingKeys.GenAiScanHost] = "clamav",
                [SettingKeys.GenAiScanPort] = "3311",
            }, k.Admin.Id);

            Assert.Equal(6, changed);
            var saved = (await k.Config.ListAsync()).ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal("https://genai.example.com", saved[SettingKeys.GenAiBaseUrl]);
            Assert.Equal("https://admin.example.com https://portal.example.com:8443", saved[SettingKeys.GenAiFrameAncestors]);
            Assert.Equal("clamav", saved[SettingKeys.GenAiScanHost]);
            Assert.Equal(before + 6, await k.Db.AuditLogs.CountAsync(a => a.Action == "config.setting-changed"));
        }
        finally { await ResetAsync(k); }
    }

    [Theory]
    [InlineData(SettingKeys.GenAiBaseUrl, "genai.example.com")]               // no https://
    [InlineData(SettingKeys.GenAiBaseUrl, "https://genai.example.com/app")]   // a path
    [InlineData(SettingKeys.GenAiBaseUrl, "https://user:pw@genai.example.com")]
    [InlineData(SettingKeys.GenAiBaseUrl, "ftp://genai.example.com")]
    [InlineData(SettingKeys.GenAiBaseUrl, "https://*.example.com")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "*")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "'none'")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "https://*.example.com")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "https://a.example.com https://b.example.com/path")]
    [InlineData(SettingKeys.GenAiLinkMinutes, "0")]
    [InlineData(SettingKeys.GenAiLinkMinutes, "5000")]
    [InlineData(SettingKeys.GenAiLinkMinutes, "soon")]
    [InlineData(SettingKeys.GenAiWarnSizeMb, "0")]
    [InlineData(SettingKeys.GenAiWarnSizeMb, "6")]
    [InlineData(SettingKeys.GenAiScanHost, "http://clamav")]
    [InlineData(SettingKeys.GenAiScanHost, "clamav:3310")]
    [InlineData(SettingKeys.GenAiScanHost, "clam av")]
    [InlineData(SettingKeys.GenAiScanPort, "0")]
    [InlineData(SettingKeys.GenAiScanPort, "70000")]
    public void Wrong_values_are_refused(string key, string value) => Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(key, value));

    [Theory]
    [InlineData(SettingKeys.GenAiBaseUrl, "HTTPS://GenAI.Example.com/", "https://genai.example.com")]
    [InlineData(SettingKeys.GenAiBaseUrl, "http://localhost:8082", "http://localhost:8082")]
    [InlineData(SettingKeys.GenAiBaseUrl, "  ", "")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "https://admin.example.com, https://portal.example.com:8443 https://admin.example.com", "https://admin.example.com https://portal.example.com:8443")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "http://localhost:8080;http://localhost:8081", "http://localhost:8080 http://localhost:8081")]
    [InlineData(SettingKeys.GenAiFrameAncestors, "", "")]
    [InlineData(SettingKeys.GenAiScanHost, "clamav", "clamav")]
    [InlineData(SettingKeys.GenAiScanHost, "10.0.0.5", "10.0.0.5")]
    [InlineData(SettingKeys.GenAiScanHost, "scan.internal.example.com", "scan.internal.example.com")]
    [InlineData(SettingKeys.GenAiScanHost, "", "")]
    [InlineData(SettingKeys.GenAiLinkMinutes, "15", "15")]
    [InlineData(SettingKeys.GenAiWarnSizeMb, "5", "5")]
    [InlineData(SettingKeys.GenAiScanPort, "3310", "3310")]
    public void Good_values_are_cleaned_up(string key, string value, string expected) => Assert.Equal(expected, ConfigService.NormaliseValue(key, value));

    [Fact]
    public void More_than_10_portal_addresses_are_refused() =>
        Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(SettingKeys.GenAiFrameAncestors, string.Join(' ', Enumerable.Range(1, 11).Select(n => $"https://p{n}.example.com"))));

    [SqlFact]
    public async Task Blank_addresses_and_scanner_fall_back_to_the_environment_and_set_values_win()
    {
        await using var k = await ArrangeAsync();
        var deployed = Options.Create(new GenAiOptions { BaseUrl = "https://env.example.com", FrameAncestors = "https://env-portal.example.com", ScanHost = "env-scanner" });
        var settings = new GenAiSettings(k.Db, deployed);
        try
        {
            var blank = await settings.GetAsync();
            Assert.Equal("https://env.example.com", blank.BaseUrl);
            Assert.Equal("https://env-portal.example.com", blank.FrameAncestors);
            Assert.Equal("env-scanner", blank.ScanHost);
            Assert.Equal(60, blank.LinkMinutes);                 // numbers always come from Admin Configuration
            Assert.Equal(2 * 1024 * 1024, blank.WarnBytes);

            await k.Config.UpdateAsync(new Dictionary<string, string?>
            {
                [SettingKeys.GenAiBaseUrl] = "https://genai.example.com", [SettingKeys.GenAiScanHost] = "clamav",
                [SettingKeys.GenAiLinkMinutes] = "10", [SettingKeys.GenAiWarnSizeMb] = "3",
            }, k.Admin.Id);

            var set = await settings.GetAsync();   // read again with no restart
            Assert.Equal("https://genai.example.com", set.BaseUrl);
            Assert.Equal("https://env-portal.example.com", set.FrameAncestors);   // still blank, still the environment's
            Assert.Equal("clamav", set.ScanHost);
            Assert.Equal(10, set.LinkMinutes);
            Assert.Equal(3 * 1024 * 1024, set.WarnBytes);
        }
        finally { await ResetAsync(k); }
    }

    [SqlFact]
    public async Task The_warning_size_and_the_scanner_follow_the_settings_without_a_restart()
    {
        await using var k = await ArrangeAsync();
        var settings = GenAiTestKit.Settings(k.Db);
        var scanner = new ConfiguredFileScanner(settings);
        try
        {
            Assert.Equal(ScanStatus.NotRequired, (await scanner.ScanAsync([1])).Status);   // no scanner host anywhere

            var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var closedPort = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            await k.Config.UpdateAsync(new Dictionary<string, string?> { [SettingKeys.GenAiScanHost] = "127.0.0.1", [SettingKeys.GenAiScanPort] = closedPort.ToString() }, k.Admin.Id);

            // Now configured, but nothing is listening: the upload is refused rather than accepted unscanned.
            await Assert.ThrowsAsync<RuleException>(() => scanner.ScanAsync([1]));
        }
        finally { await ResetAsync(k); }
    }
}
