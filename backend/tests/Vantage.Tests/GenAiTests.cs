using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.GenAi;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>Builds a <see cref="GenAiService"/> over a throwaway file store, for tests that also need an EmbedService.</summary>
internal static class GenAiTestKit
{
    public static GenAiService Service(AppDbContext db, AuditWriter audit, TimeProvider clock, int linkMinutes = 60, IFileStore? files = null, IFileScanner? scanner = null) =>
        new(db, files ?? new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N"))),
            DataProtectionProvider.Create("vantage-tests"), Options.Create(new GenAiOptions { BaseUrl = "http://genai.test/", LinkMinutes = linkMinutes }),
            audit, new NotificationService(db, clock), scanner ?? new NoFileScanner(), clock);
}

/// <summary>GenAI dashboards: publishing, Modify Dashboard, restore, signed links and what gets served.</summary>
public class GenAiTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private sealed class NoPowerBi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class NoTokens : IPowerBiTokenProvider
    {
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) => Task.FromResult("t");
    }

    private sealed record Kit(AppDbContext Db, GenAiService GenAi, GenAiPublisher Publisher, EmbedService Embed, User Owner, User Member, User Admin, int CategoryId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static byte[] Html(string marker = "ok") =>
        Encoding.UTF8.GetBytes($"""<!DOCTYPE html><html><head><meta name="vantage-template" content="genai-1"><title>{marker}</title></head><body>{marker}</body></html>""");

    private async Task<Kit> ArrangeAsync(int linkMinutes = 60, IFileScanner? scanner = null)
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var files = new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));
        var genAi = GenAiTestKit.Service(db, audit, clock, linkMinutes, files, scanner);

        var owner = new User { Email = Unique("o") + "@rrd.com", DisplayName = Unique("Owner "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var member = new User { Email = Unique("m") + "@rrd.com", DisplayName = Unique("Member "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var admin = new User { Email = Unique("a") + "@rrd.com", DisplayName = Unique("Admin "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var category = new Category { Name = Unique("Cat "), Level = 1, CreatedAtUtc = DateTime.UtcNow };
        db.AddRange(owner, member, admin, category);
        await db.SaveChangesAsync();

        var powerBi = new PowerBiClient(new HttpClient(new NoPowerBi()), new NoTokens(), NullLogger<PowerBiClient>.Instance);
        var ownership = new OwnershipService(db, clock);
        var master = new DashboardMasterService(db, ownership, powerBi, files, audit, clock);
        var factory = new DashboardFactory(db, ownership, audit, clock);
        var publisher = new GenAiPublisher(db, factory, master, genAi, audit, new NotificationService(db, clock), clock);
        var embed = new EmbedService(db, powerBi, new TableauTokenService(null!, clock), genAi, audit, clock);
        return new Kit(db, genAi, publisher, embed, owner, member, admin, category.Id);
    }

    private static PublishGenAiRequest Request(Kit k, string? name = null) =>
        new(name ?? Unique("GenAI "), "GEN", "A GenAI page", k.Owner.Id, null, k.CategoryId, "page.html");

    private static async Task<PublishStatus> PublishAsync(Kit k, byte[]? html = null, string? name = null) =>
        await k.Publisher.PublishAsync(Request(k, name), new MemoryStream(html ?? Html()), k.Owner.Id);

    private async Task AddMemberAsync(Kit k, int dashboardId)
    {
        var group = await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == dashboardId && g.IsDefault);
        k.Db.GroupMembers.Add(new GroupMember { GroupId = group.Id, DashboardId = dashboardId, UserId = k.Member.Id, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow });
        await k.Db.SaveChangesAsync();
    }

    private static async Task<string> ReadAsync(GenAiContent c)
    {
        await using var s = c.Content;
        return await new StreamReader(s).ReadToEndAsync();
    }

    [SqlFact]
    public async Task Publishing_makes_an_active_genai_dashboard_with_version_1_and_the_owners_group()
    {
        await using var k = await ArrangeAsync();

        var status = await PublishAsync(k);

        Assert.Equal("Active", status.Status);
        var d = await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == status.DashboardId);
        Assert.Equal(DashboardType.GenAi, d.Type);
        Assert.False(d.RlsEnabled);
        Assert.NotNull(d.PublishedAtUtc);
        var v = await k.Db.DashboardVersions.AsNoTracking().SingleAsync(x => x.DashboardId == d.Id);
        Assert.True(v.IsCurrent);
        Assert.Equal(1, v.VersionNumber);
        Assert.Equal($"GEN-{d.Id}-default", status.DefaultGroup);
        Assert.True(await k.Db.GroupMembers.AnyAsync(m => m.DashboardId == d.Id && m.UserId == k.Owner.Id && m.RemovedAtUtc == null));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.DashboardId == d.Id && a.Action == "dashboard.published"));
    }

    [SqlFact]
    public async Task A_file_that_fails_the_checks_creates_nothing()
    {
        await using var k = await ArrangeAsync();
        var name = Unique("Bad ");
        var bad = Encoding.UTF8.GetBytes("""<html><head><meta name="vantage-template" content="genai-1"><script src="https://evil.example.com/x.js"></script></head></html>""");

        var ex = await Assert.ThrowsAsync<RuleException>(() => PublishAsync(k, bad, name));

        Assert.Contains("evil.example.com", ex.Message);
        Assert.False(await k.Db.Dashboards.AnyAsync(d => d.Name == name));
    }

    [SqlFact]
    public async Task Only_html_files_are_accepted()
    {
        await using var k = await ArrangeAsync();

        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Request(k) with { FileName = "page.pbix" }, new MemoryStream(Html()), k.Owner.Id));
    }

    [SqlFact]
    public async Task A_switched_off_genai_type_refuses_publishing()
    {
        await using var k = await ArrangeAsync();
        var type = await k.Db.BiServiceTypes.SingleAsync(t => t.Type == DashboardType.GenAi);
        type.IsEnabled = false;
        await k.Db.SaveChangesAsync();
        try
        {
            await Assert.ThrowsAsync<RuleException>(() => PublishAsync(k));
        }
        finally
        {
            type.IsEnabled = true;
            await k.Db.SaveChangesAsync();
        }
    }

    [SqlFact]
    public async Task Modify_goes_live_at_once_and_only_the_last_3_versions_are_kept()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;

        for (var i = 2; i <= 5; i++)
        {
            var r = await k.GenAi.ReplaceAsync(id, new MemoryStream(Html("v" + i)), "page.html", k.Owner.Id);
            Assert.Equal("Succeeded", r.State);
            Assert.Equal(i, r.VersionNumber);
        }

        var versions = await k.Db.DashboardVersions.AsNoTracking().Where(v => v.DashboardId == id).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal([3, 4, 5], versions.Select(v => v.VersionNumber));
        Assert.Equal([false, false, true], versions.Select(v => v.IsCurrent));

        var (url, _) = await k.GenAi.LinkAsync(await k.Db.Dashboards.SingleAsync(d => d.Id == id));
        Assert.Contains(">v5<", await ReadAsync((await k.GenAi.OpenAsync(url[(url.LastIndexOf('/') + 1)..]))!));
    }

    [SqlFact]
    public async Task A_replacement_that_fails_the_checks_leaves_the_live_version_alone()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;

        await Assert.ThrowsAsync<RuleException>(() => k.GenAi.ReplaceAsync(id, new MemoryStream(Encoding.UTF8.GetBytes("<html>no marker</html>")), "x.html", k.Owner.Id));

        var v = await k.Db.DashboardVersions.AsNoTracking().SingleAsync(x => x.DashboardId == id);
        Assert.Equal(1, v.VersionNumber);
        Assert.True(v.IsCurrent);
    }

    [SqlFact]
    public async Task Restore_makes_an_older_file_the_new_live_version()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k, Html("first"))).DashboardId;
        await k.GenAi.ReplaceAsync(id, new MemoryStream(Html("second")), "page.html", k.Owner.Id);

        var r = await k.GenAi.RestoreAsync(id, 1, k.Owner.Id);

        Assert.Equal(3, r.VersionNumber);
        var live = await k.Db.DashboardVersions.AsNoTracking().SingleAsync(v => v.DashboardId == id && v.IsCurrent);
        Assert.Equal(3, live.VersionNumber);
        Assert.Equal("Restored from version 1", live.ScanReport);
        await Assert.ThrowsAsync<RuleException>(() => k.GenAi.RestoreAsync(id, 3, k.Owner.Id)); // already live
    }

    [SqlFact]
    public async Task A_restore_is_refused_when_the_old_file_uses_a_cdn_that_is_no_longer_approved()
    {
        await using var k = await ArrangeAsync();
        var host = Unique("old-cdn-") + ".example.net";
        var cdn = new ApprovedCdn { Host = host, IsActive = true };
        k.Db.ApprovedCdns.Add(cdn);
        await k.Db.SaveChangesAsync();
        var withCdn = Encoding.UTF8.GetBytes($"""<html><head><meta name="vantage-template" content="genai-1"><script src="https://{host}/x.js" integrity="sha384-zYPBGXwO4633CABX/5Spf6emCKUJCfoOkhOMYyxMsatqQZPnDblmmOewfjsIVWCM" crossorigin="anonymous"></script></head></html>""");
        var id = (await PublishAsync(k, withCdn)).DashboardId;
        await k.GenAi.ReplaceAsync(id, new MemoryStream(Html("second")), "page.html", k.Owner.Id);
        cdn.IsActive = false;
        await k.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<RuleException>(() => k.GenAi.RestoreAsync(id, 1, k.Owner.Id));

        Assert.Contains("no longer passes", ex.Message);
    }

    [SqlFact]
    public async Task A_member_gets_a_signed_link_and_it_counts_as_a_view()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;
        await AddMemberAsync(k, id);

        var info = await k.Embed.GetEmbedAsync(id, k.Member.Id, default);

        Assert.Equal("genai", info.Type);
        Assert.StartsWith("http://genai.test/", info.EmbedUrl);
        Assert.Null(info.Token);
        Assert.NotNull(info.ExpiresAt);
        Assert.True(await k.Db.DashboardViews.AnyAsync(v => v.DashboardId == id && v.UserId == k.Member.Id));

        var content = await k.GenAi.OpenAsync(info.EmbedUrl![info.EmbedUrl.LastIndexOf('/')..].TrimStart('/'));
        Assert.NotNull(content);
        Assert.Contains("sandbox allow-scripts", content!.ContentSecurityPolicy);
        Assert.Contains("ok", await ReadAsync(content));
    }

    [SqlFact]
    public async Task Someone_outside_the_groups_gets_no_link()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;

        await Assert.ThrowsAsync<NoAccessException>(() => k.Embed.GetEmbedAsync(id, k.Member.Id, default));
    }

    [SqlFact]
    public async Task A_super_admin_preview_gets_a_link_but_is_not_a_view()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;
        var group = await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == id && g.IsDefault);

        var info = await k.Embed.PreviewAsync(id, group.Id, k.Admin.Id, default);

        Assert.Equal("genai", info.Type);
        Assert.False(await k.Db.DashboardViews.AnyAsync(v => v.DashboardId == id));
        Assert.False(await k.Db.GroupMembers.AnyAsync(m => m.UserId == k.Admin.Id));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.DashboardId == id && a.Action == "dashboard.previewed"));
    }

    [SqlFact]
    public async Task Links_that_are_forged_expired_or_for_a_retired_dashboard_serve_nothing()
    {
        await using var k = await ArrangeAsync();
        var id = (await PublishAsync(k)).DashboardId;
        var d = await k.Db.Dashboards.SingleAsync(x => x.Id == id);
        var (url, _) = await k.GenAi.LinkAsync(d);
        var token = url[(url.LastIndexOf('/') + 1)..];

        Assert.NotNull(await k.GenAi.OpenAsync(token));
        Assert.Null(await k.GenAi.OpenAsync("not-a-token"));
        Assert.Null(await k.GenAi.OpenAsync(token + "x"));

        d.Status = DashboardStatus.Retired;
        await k.Db.SaveChangesAsync();
        Assert.Null(await k.GenAi.OpenAsync(token));

        await using var expired = await ArrangeAsync(linkMinutes: -1);
        var id2 = (await PublishAsync(expired)).DashboardId;
        var (url2, _) = await expired.GenAi.LinkAsync(await expired.Db.Dashboards.SingleAsync(x => x.Id == id2));
        Assert.Null(await expired.GenAi.OpenAsync(url2[(url2.LastIndexOf('/') + 1)..]));
    }

    private sealed class FakeScanner(ScanStatus status, string? report) : IFileScanner
    {
        public int Calls;
        public Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default) { Calls++; return Task.FromResult(new ScanOutcome(status, report)); }
    }

    [SqlFact]
    public async Task A_file_the_malware_scanner_flags_is_refused_and_creates_nothing()
    {
        var scanner = new FakeScanner(ScanStatus.Failed, "Malware scan (ClamAV) flagged the file: Eicar-Test-Signature.");
        await using var k = await ArrangeAsync(scanner: scanner);
        var name = Unique("Flagged ");

        var ex = await Assert.ThrowsAsync<RuleException>(() => PublishAsync(k, Html(), name));

        Assert.Contains("Eicar", ex.Message);
        Assert.Equal(1, scanner.Calls);
        Assert.False(await k.Db.Dashboards.AnyAsync(d => d.Name == name));
    }

    [SqlFact]
    public async Task A_clean_scan_is_kept_with_the_version_and_a_restore_is_scanned_again()
    {
        var scanner = new FakeScanner(ScanStatus.Passed, "Malware scan (ClamAV): clean.");
        await using var k = await ArrangeAsync(scanner: scanner);
        var id = (await PublishAsync(k)).DashboardId;
        await k.GenAi.ReplaceAsync(id, new MemoryStream(Html("second")), "page.html", k.Owner.Id);

        await k.GenAi.RestoreAsync(id, 1, k.Owner.Id);

        Assert.Equal(3, scanner.Calls); // publish, replace, restore
        var live = await k.Db.DashboardVersions.AsNoTracking().SingleAsync(v => v.DashboardId == id && v.IsCurrent);
        Assert.Equal(ScanStatus.Passed, live.ScanStatus);
        Assert.Contains("clean", live.ScanReport);
    }
}
