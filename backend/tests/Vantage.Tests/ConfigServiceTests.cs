using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>Admin Configuration: validated settings, the logo, approved CDNs, BI types, and the audit trail of all of it.</summary>
public class ConfigServiceTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private sealed class MemoryFiles : IFileStore
    {
        public readonly Dictionary<string, byte[]> Saved = [];
        public async Task<(long Size, string Sha256)> SaveAsync(string key, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Saved[key] = ms.ToArray();
            return (ms.Length, "");
        }
        public Stream OpenRead(string key) => new MemoryStream(Saved[key]);
        public void Delete(string key) => Saved.Remove(key);
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, ConfigService Config, MemoryFiles Files, User Admin) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var files = new MemoryFiles();
        var admin = new User { Email = Unique("a") + "@rrd.com", DisplayName = Unique("Admin "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        return new Kit(db, new ConfigService(db, new AuditWriter(db, new Ctx(), TimeProvider.System), files, TimeProvider.System), files, admin);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private static Task<int> ChangesAudited(AppDbContext db, string action) => db.AuditLogs.CountAsync(a => a.Action == action);

    [SqlFact]
    public async Task Valid_settings_are_saved_and_audited_and_wrong_ones_save_nothing()
    {
        await using var k = await ArrangeAsync();
        var before = await ChangesAudited(k.Db, "config.setting-changed");

        var bad = new[]
        {
            (SettingKeys.IdleTimeoutMinutes, "2"), (SettingKeys.IdleTimeoutMinutes, "soon"), (SettingKeys.BrandPrimaryColor, "blue"), (SettingKeys.HrmsSyncCron, "every day"),
            (SettingKeys.InternalEmailDomain, "not a domain"), (SettingKeys.NewHireDigestFrequency, "Hourly"), (SettingKeys.BrandPortalName, "  "), (SettingKeys.ToastSeconds, "1"), (SettingKeys.ToastSeconds, "31"), (SettingKeys.ExternalSeeInternalCatalogue, "maybe"),
        };
        foreach (var (key, value) in bad)
            await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateAsync(new Dictionary<string, string?> { [key] = value }, k.Admin.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateAsync(new Dictionary<string, string?> { ["not.a.setting"] = "x" }, k.Admin.Id));
        // One wrong value stops the whole save, even next to a good one.
        await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateAsync(new Dictionary<string, string?> { [SettingKeys.IdleTimeoutMinutes] = "45", [SettingKeys.BrandPrimaryColor] = "nope" }, k.Admin.Id));
        Assert.Equal(before, await ChangesAudited(k.Db, "config.setting-changed"));
        Assert.Equal(30, (await k.Config.UiAsync()).IdleTimeoutMinutes);
        Assert.Equal(6, (await k.Config.UiAsync()).ToastSeconds);

        var changed = await k.Config.UpdateAsync(new Dictionary<string, string?>
        {
            [SettingKeys.IdleTimeoutMinutes] = " 45 ", [SettingKeys.BrandPrimaryColor] = "#1f4e79", [SettingKeys.DefaultGridPageSize] = "50", [SettingKeys.ToastSeconds] = "10",
            [SettingKeys.InternalEmailDomain] = "@RRD.com", [SettingKeys.HrmsSyncCron] = "0  3 * * 1", [SettingKeys.ExternalSeeInternalCatalogue] = "TRUE",
        }, k.Admin.Id);

        Assert.Equal(5, changed);                                    // the colour and the domain already had those values once tidied, so they aren't changes
        Assert.Equal(before + 5, await ChangesAudited(k.Db, "config.setting-changed"));
        var ui = await k.Config.UiAsync();
        Assert.Equal(45, ui.IdleTimeoutMinutes);
        Assert.Equal(50, ui.GridPageSize);
        Assert.Equal(10, ui.ToastSeconds);
        var list = await k.Config.ListAsync();
        Assert.Equal("rrd.com", list.Single(s => s.Key == SettingKeys.InternalEmailDomain).Value);
        Assert.Equal("0 3 * * 1", list.Single(s => s.Key == SettingKeys.HrmsSyncCron).Value);
        Assert.Equal("true", list.Single(s => s.Key == SettingKeys.ExternalSeeInternalCatalogue).Value);
        Assert.Equal(k.Admin.DisplayName, list.Single(s => s.Key == SettingKeys.IdleTimeoutMinutes).UpdatedBy);
        Assert.True(list.Single(s => s.Key == SettingKeys.IdleTimeoutMinutes).InUse);
        Assert.True(list.Single(s => s.Key == SettingKeys.HrmsSyncCron).InUse);        // the scheduled jobs use it now
        Assert.Contains(await k.Db.AuditLogs.Where(a => a.Action == "config.setting-changed").ToListAsync(), a => a.Details!.Contains("45") && a.Details.Contains("30"));
        Assert.DoesNotContain(list, s => s.Key == SettingKeys.BrandLogoFile);          // internal keys are never offered
    }

    [SqlFact]
    public async Task The_logo_must_be_a_small_safe_image_and_replacing_it_removes_the_old_file()
    {
        await using var k = await ArrangeAsync();
        await Assert.ThrowsAsync<RuleException>(() => k.Config.SaveLogoAsync(new MemoryStream("hello"u8.ToArray()), "x.png", k.Admin.Id));                          // not an image
        await Assert.ThrowsAsync<RuleException>(() => k.Config.SaveLogoAsync(new MemoryStream("<svg onload=\"x()\"></svg>"u8.ToArray()), "x.svg", k.Admin.Id));     // script in an svg
        await Assert.ThrowsAsync<RuleException>(() => k.Config.SaveLogoAsync(new MemoryStream("<svg><script>x()</script></svg>"u8.ToArray()), "x.svg", k.Admin.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Config.SaveLogoAsync(new MemoryStream([.. Png, .. new byte[ConfigService.LogoMaxBytes]]), "big.png", k.Admin.Id)); // too big
        Assert.Empty(k.Files.Saved);

        await k.Config.SaveLogoAsync(new MemoryStream(Png), "logo.png", k.Admin.Id);
        var first = Assert.Single(k.Files.Saved.Keys);
        Assert.EndsWith(".png", first);
        var url = (await k.Db.SystemSettings.AsNoTracking().SingleAsync(s => s.Key == SettingKeys.BrandLogoUrl)).Value;
        Assert.StartsWith("/api/branding/logo?v=", url);

        await k.Config.SaveLogoAsync(new MemoryStream("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray()), "logo.svg", k.Admin.Id);
        Assert.Single(k.Files.Saved);                                                  // the old one was deleted
        Assert.DoesNotContain(first, k.Files.Saved.Keys);
        Assert.EndsWith(".svg", k.Files.Saved.Keys.Single());

        await k.Config.ResetLogoAsync(k.Admin.Id);
        Assert.Empty(k.Files.Saved);
        Assert.Equal(SettingKeys.DefaultLogoUrl, (await k.Db.SystemSettings.AsNoTracking().SingleAsync(s => s.Key == SettingKeys.BrandLogoUrl)).Value);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "config.logo-changed"));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "config.logo-reset"));
    }

    [SqlFact]
    public async Task Approved_CDNs_are_plain_unique_host_names()
    {
        await using var k = await ArrangeAsync();
        var host = Unique("cdn") + ".example.com";

        var added = await k.Config.AddCdnAsync("  " + host.ToUpperInvariant() + " ", "Pin versions");
        Assert.Equal(host, added.Host);
        await Assert.ThrowsAsync<RuleException>(() => k.Config.AddCdnAsync(host, null));                                    // already there
        foreach (var bad in new[] { "", "https://x.example.com", "x.example.com/lib", "x.example.com:8080", "*.example.com", "localhost", "has space.com" })
            await Assert.ThrowsAsync<RuleException>(() => k.Config.AddCdnAsync(bad, null));

        await k.Config.UpdateCdnAsync(added.Id, "Pinned", false);
        Assert.False((await k.Config.CdnsAsync()).Single(c => c.Id == added.Id).IsActive);
        await k.Config.RemoveCdnAsync(added.Id);
        Assert.DoesNotContain(await k.Config.CdnsAsync(), c => c.Id == added.Id);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "config.cdn-added" && a.Details!.Contains(host)));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "config.cdn-removed" && a.Details!.Contains(host)));
    }

    [SqlFact]
    public async Task Dashboard_types_can_be_switched_off_but_never_all_of_them_and_the_limits_are_enforced()
    {
        await using var k = await ArrangeAsync();
        // The database is seeded with the three types; make sure they are all there and switched on, with known limits.
        foreach (var t in new[]
        {
            new BiTypeConfig { Type = BiType.PowerBi, DisplayName = "Power BI", RequiresFile = true, AllowedExtensions = ".pbix", MaxFileSizeMb = 1024 },
            new BiTypeConfig { Type = BiType.Tableau, DisplayName = "Tableau", RequiresUrl = true },
            new BiTypeConfig { Type = BiType.GenAi, DisplayName = "GenAI Dashboard", RequiresFile = true, AllowedExtensions = ".html", MaxFileSizeMb = 5 },
        })
        {
            var row = await k.Db.BiTypes.SingleOrDefaultAsync(x => x.Type == t.Type);
            if (row is null) k.Db.BiTypes.Add(t);
            else { row.IsEnabled = true; row.MaxFileSizeMb = t.MaxFileSizeMb; }
        }
        await k.Db.SaveChangesAsync();

        await ServiceTypes.EnsureAllowedAsync(k.Db, BiType.PowerBi, 10L * 1024 * 1024);                       // fine at first
        await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateTypeAsync(BiType.PowerBi, true, false, 0));       // limit out of range
        await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateTypeAsync(BiType.PowerBi, true, false, 5000));

        await k.Config.UpdateTypeAsync(BiType.PowerBi, true, false, 50);
        await ServiceTypes.EnsureAllowedAsync(k.Db, BiType.PowerBi, 50L * 1024 * 1024);
        var tooBig = await Assert.ThrowsAsync<RuleException>(() => ServiceTypes.EnsureAllowedAsync(k.Db, BiType.PowerBi, 51L * 1024 * 1024));
        Assert.Contains("50 MB", tooBig.Message);

        await k.Config.UpdateTypeAsync(BiType.PowerBi, false, false, 50);
        var off = await Assert.ThrowsAsync<RuleException>(() => ServiceTypes.EnsureAllowedAsync(k.Db, BiType.PowerBi, 1));
        Assert.Contains("switched off", off.Message);
        await k.Config.UpdateTypeAsync(BiType.Tableau, false, false, null);
        var state = (await k.Config.UiAsync()).BiTypes;
        Assert.False(state.Single(x => x.Type == "PowerBi").Enabled);
        Assert.False(state.Single(x => x.Type == "Tableau").HideExisting);
        await k.Config.UpdateTypeAsync(BiType.Tableau, false, true, null);
        Assert.True((await k.Config.UiAsync()).BiTypes.Single(x => x.Type == "Tableau").HideExisting);
        Assert.True((await k.Config.TypesAsync()).Single(x => x.Type == "Tableau").HideWhenInactive);
        await Assert.ThrowsAsync<RuleException>(() => k.Config.UpdateTypeAsync(BiType.GenAi, false, false, 5));       // the last one stays on
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "config.service-type-updated" && a.Details!.Contains("Power BI")));
    }
}
