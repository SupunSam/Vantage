using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Secrets;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>Reading Tableau view addresses is pure code, so these run everywhere.</summary>
public class TableauViewUrlTests
{
    private const string Server = "https://tableau.example.com";

    [Theory]
    [InlineData("https://public.tableau.com/views/Sales2026/Overview", "Sales2026", "Overview")]
    [InlineData("https://public.tableau.com/views/Sales2026/Overview?:language=en-US&:display_count=n&:origin=viz_share_link", "Sales2026", "Overview")]
    [InlineData("https://PUBLIC.tableau.com/app/profile/jane.doe/viz/Sales2026/Overview#1", "Sales2026", "Overview")]
    [InlineData("  https://public.tableau.com/views/Sales_2026/Sheet%201  ", "Sales_2026", "Sheet%201")]
    public void Tableau_public_addresses_are_cleaned_to_the_embed_form(string text, string workbook, string view)
    {
        var v = TableauViewUrl.Parse(text, null, null);
        Assert.True(v.IsPublic);
        Assert.Equal($"https://public.tableau.com/views/{workbook}/{view}", v.Src);
        Assert.Equal(workbook, v.Workbook);
        Assert.Equal(view, v.View);
        Assert.Equal("https://public.tableau.com/javascripts/api/tableau.embedding.3.latest.min.js", TableauViewUrl.ScriptUrl(v, null));
        Assert.Equal(v.Src, TableauViewUrl.Parse(v.Src, "", null).Src);                  // already clean: unchanged, and a blank server means public
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("public.tableau.com/views/A/B")]                      // no scheme
    [InlineData("http://public.tableau.com/views/A/B")]               // not https
    [InlineData("https://user:pw@public.tableau.com/views/A/B")]      // login details
    [InlineData("https://tableau.example.com/views/A/B")]             // a server address, but no tenant chosen
    [InlineData("https://public.tableau.com.evil.com/views/A/B")]     // look-alike host
    [InlineData("https://public.tableau.com:8443/views/A/B")]
    [InlineData("https://public.tableau.com/shared/ABC123")]          // a shared link, not a view
    [InlineData("https://public.tableau.com/views/A")]
    [InlineData("https://public.tableau.com/views/A/B/C")]
    [InlineData("https://public.tableau.com/views/A<b>/B")]
    public void Anything_that_is_not_a_tableau_public_view_is_refused_with_a_reason(string text) =>
        Assert.Throws<RuleException>(() => TableauViewUrl.Parse(text, null, null));

    [Theory]
    [InlineData("https://tableau.example.com/views/Sales/Overview", "", "https://tableau.example.com/views/Sales/Overview")]
    [InlineData("https://tableau.example.com/#/views/Sales/Overview?:iid=1", "", "https://tableau.example.com/views/Sales/Overview")]
    [InlineData("https://tableau.example.com/#/site/ops/views/Sales/Overview", "ops", "https://tableau.example.com/t/ops/views/Sales/Overview")]
    [InlineData("https://tableau.example.com/t/ops/views/Sales/Overview", "OPS", "https://tableau.example.com/t/OPS/views/Sales/Overview")]
    [InlineData("https://Tableau.Example.com:443/views/Sales/Overview", "", "https://tableau.example.com/views/Sales/Overview")]
    public void Tableau_server_addresses_become_the_embed_form_on_the_tenants_site(string text, string site, string expected)
    {
        var v = TableauViewUrl.Parse(text, Server, site);
        Assert.False(v.IsPublic);
        Assert.Equal(expected, v.Src);
        Assert.Equal("tableau.example.com", v.Host);
        Assert.Equal("https://tableau.example.com/javascripts/api/tableau.embedding.3.latest.min.js", TableauViewUrl.ScriptUrl(v, Server + "/"));
    }

    [Theory]
    [InlineData("https://other.example.com/views/A/B", "", "has to be on the tenant")]                // another server
    [InlineData("https://tableau.example.com/t/ops/views/A/B", "", "Default site")]                    // tenant is the default site, view is on "ops"
    [InlineData("https://tableau.example.com/views/A/B", "ops", "has no site")]                       // tenant is for "ops", address has none
    [InlineData("https://tableau.example.com/t/sales/views/A/B", "ops", "site “sales”")]
    [InlineData("https://tableau.example.com/#/projects/12", "", "isn't a Tableau view")]
    [InlineData("https://public.tableau.com/views/A/B", "", "has to be on the tenant")]               // a public address is not a server view
    public void A_server_view_must_be_on_the_tenants_server_and_site(string text, string site, string reason)
    {
        var ex = Assert.Throws<RuleException>(() => TableauViewUrl.Parse(text, Server, site));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void An_address_over_the_stored_length_is_refused()
    {
        var long_ = $"https://public.tableau.com/views/{new string('a', 150)}/{new string('b', 150)}";
        Assert.Throws<RuleException>(() => TableauViewUrl.Parse(long_, null, null));
    }

    [Fact]
    public void Only_public_data_may_go_on_tableau_public()
    {
        TableauPublisher.EnsureClassificationFits(true, DataClassification.Public);
        foreach (var c in new[] { DataClassification.Internal, DataClassification.Confidential, DataClassification.Restricted })
            Assert.Throws<RuleException>(() => TableauPublisher.EnsureClassificationFits(true, c));
        TableauPublisher.EnsureClassificationFits(false, DataClassification.Restricted);   // a Tableau Server view may carry any classification
    }

    [Fact]
    public void The_connected_app_token_names_the_viewer_and_the_embed_scope_and_expires_in_minutes()
    {
        var now = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        var (token, expires) = TableauTokenService.Sign("client-1", "secret-1", "0123456789abcdef0123456789abcdef", "jane.doe", now);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("client-1", jwt.Issuer);
        Assert.Equal("jane.doe", jwt.Subject);
        Assert.Contains("tableau", jwt.Audiences);
        Assert.Equal("secret-1", jwt.Header.Kid);
        Assert.Contains("tableau:views:embed", jwt.Claims.Where(c => c.Type == "scp").Select(c => c.Value));
        Assert.Equal(now.Add(TableauTokenService.Lifetime), expires);
        Assert.True(TableauTokenService.Lifetime <= TimeSpan.FromMinutes(10));              // Tableau's own limit
    }
}

/// <summary>Publishing Tableau dashboards and opening them, against a real database. Needs VANTAGE_TEST_SQL.</summary>
public class TableauTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private sealed class Tokens : IPowerBiTokenProvider
    {
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) => Task.FromResult("t");
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, TableauPublisher Publisher, EmbedService Embed, DashboardMasterService Master, ISecretStore Secrets, User Owner, User Member, User Admin) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync(string? memberTableauName = null)
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        User Person(string kind, string? tableau = null) => new() { Email = Unique(kind) + "@rrd.com", DisplayName = Unique(kind + " "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow, TableauUserName = tableau };
        var owner = Person("o");
        var member = Person("m", memberTableauName);
        var admin = Person("a", "admin.tableau");
        db.Users.AddRange(owner, member, admin);
        await db.SaveChangesAsync();

        var ownership = new OwnershipService(db, clock);
        var powerBi = new PowerBiClient(new HttpClient(), new Tokens(), NullLogger<PowerBiClient>.Instance);
        var files = new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));
        var master = new DashboardMasterService(db, ownership, powerBi, files, audit, clock);
        var publisher = new TableauPublisher(db, new DashboardFactory(db, ownership, audit, clock), master, audit, new NotificationService(db, clock), clock);
        var secrets = new DbSecretStore(db, new EphemeralDataProtectionProvider(), clock);
        var embed = new EmbedService(db, powerBi, new TableauTokenService(secrets, clock), GenAiTestKit.Service(db, audit, clock), audit, clock);
        return new Kit(db, publisher, embed, master, secrets, owner, member, admin);
    }

    private static PublishTableauRequest Public(Kit k, DataClassification c = DataClassification.Public, string? url = null) =>
        new(Unique("Tab "), "TB", null, null, url ?? "https://public.tableau.com/views/Sales2026/Overview?:language=en-US", k.Owner.Id, null, null, null, Audience.Internal, c);

    private async Task<BiTenant> TenantAsync(AppDbContext db, string? site = "", bool withSecret = true, Kit? k = null)
    {
        var t = new BiTenant
        {
            Name = Unique("Tab "), Platform = BiPlatform.Tableau, ServerUrl = "https://tableau.example.com", SiteContentUrl = site,
            ConnectedAppClientId = "client-1", ConnectedAppSecretId = "secret-1", SecretName = Unique("tableau-secret-"),
        };
        db.BiTenants.Add(t);
        await db.SaveChangesAsync();
        if (withSecret) await k!.Secrets.SetAsync(t.SecretName!, "0123456789abcdef0123456789abcdef", k.Admin.Id, default);
        return t;
    }

    private static async Task AddMemberAsync(AppDbContext db, int dashboardId, int userId)
    {
        var group = await db.DashboardGroups.SingleAsync(g => g.DashboardId == dashboardId && g.IsDefault);
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, DashboardId = dashboardId, UserId = userId, Source = MembershipSource.Manual, AddedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ Tableau Public

    [SqlFact]
    public async Task A_tableau_public_dashboard_is_live_at_once_with_a_clean_address_the_owners_in_its_group_and_no_tenant()
    {
        await using var k = await ArrangeAsync();
        var status = await k.Publisher.PublishAsync(Public(k), k.Owner.Id);

        Assert.Equal("Active", status.Status);
        Assert.Contains("anyone who has its address", status.Warning);
        var d = await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == status.DashboardId);
        Assert.Equal(DashboardType.Tableau, d.Type);
        Assert.Equal(DashboardStatus.Active, d.Status);
        Assert.Null(d.TenantId);
        Assert.False(d.RlsEnabled);
        Assert.Equal("https://public.tableau.com/views/Sales2026/Overview", d.TableauViewUrl);   // the share-link options are dropped
        Assert.NotNull(d.PublishedAtUtc);
        Assert.True(await k.Db.GroupMembers.AnyAsync(m => m.DashboardId == d.Id && m.UserId == k.Owner.Id && m.RemovedAtUtc == null && m.Group.IsDefault));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "dashboard.published" && a.DashboardId == d.Id && a.Details!.Contains("Public")));
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "publish.succeeded" && n.Link == $"/dashboards/{d.Id}"));
    }

    [SqlFact]
    public async Task Tableau_public_needs_public_data_and_a_refused_publish_leaves_nothing_behind()
    {
        await using var k = await ArrangeAsync();
        var req = Public(k, DataClassification.Internal);

        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(req, k.Owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Public(k, url: "https://tableau.example.com/views/A/B"), k.Owner.Id));

        Assert.False(await k.Db.Dashboards.AnyAsync(d => d.Name == req.Name));
    }

    [SqlFact]
    public async Task Members_open_a_tableau_public_dashboard_with_no_token_and_it_counts_as_a_view_while_a_preview_does_not()
    {
        await using var k = await ArrangeAsync();
        var d = await k.Publisher.PublishAsync(Public(k), k.Owner.Id);
        await AddMemberAsync(k.Db, d.DashboardId, k.Member.Id);
        var group = await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == d.DashboardId && g.IsDefault);

        var info = await k.Embed.GetEmbedAsync(d.DashboardId, k.Member.Id, default);

        Assert.Equal("tableau", info.Type);
        Assert.Equal("https://public.tableau.com/views/Sales2026/Overview", info.EmbedUrl);
        Assert.Null(info.Token);
        Assert.Equal("https://public.tableau.com/javascripts/api/tableau.embedding.3.latest.min.js", info.TableauScriptUrl);
        Assert.Equal(1, await k.Db.DashboardViews.CountAsync(v => v.DashboardId == d.DashboardId));

        await k.Embed.PreviewAsync(d.DashboardId, group.Id, k.Admin.Id, default);
        Assert.Equal(1, await k.Db.DashboardViews.CountAsync(v => v.DashboardId == d.DashboardId));       // a preview is not a view
        await Assert.ThrowsAsync<NoAccessException>(() => k.Embed.GetEmbedAsync(d.DashboardId, k.Admin.Id, default)); // and Super Admins still need a group to consume
    }

    [SqlFact]
    public async Task A_public_tableau_dashboards_classification_cannot_be_raised_and_the_view_cannot_move_to_a_server_it_does_not_fit()
    {
        await using var k = await ArrangeAsync();
        var d = await k.Publisher.PublishAsync(Public(k), k.Owner.Id);
        var dashboard = await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.DashboardId);

        await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.DashboardId, new DashboardDetailsInput(
            dashboard.Name, null, null, k.Owner.Id, null, null, Audience.Internal, DataClassification.Confidential, null, null), k.Admin.Id));

        var tenant = await TenantAsync(k.Db, "", false);
        await Assert.ThrowsAsync<RuleException>(() => k.Master.SetTableauViewAsync(d.DashboardId, tenant.Id, "https://public.tableau.com/views/A/B", k.Admin.Id));   // not on that server
    }

    // ------------------------------------------------------------ Tableau Server

    [SqlFact]
    public async Task A_tableau_server_dashboard_stores_the_embed_address_on_the_tenants_site_and_warns_until_the_tenant_is_verified()
    {
        await using var k = await ArrangeAsync();
        var tenant = await TenantAsync(k.Db, "ops", false);

        var status = await k.Publisher.PublishAsync(Public(k, DataClassification.Confidential, "https://tableau.example.com/#/site/ops/views/Sales/Overview") with { TenantId = tenant.Id }, k.Owner.Id);

        var d = await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == status.DashboardId);
        Assert.Equal(tenant.Id, d.TenantId);
        Assert.Equal("https://tableau.example.com/t/ops/views/Sales/Overview", d.TableauViewUrl);
        Assert.Contains("hasn't passed Verify", status.Warning);

        await k.Db.BiTenants.Where(t => t.Id == tenant.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.LastVerifyPassed, true));
        var second = await k.Publisher.PublishAsync(Public(k, DataClassification.Internal, "https://tableau.example.com/t/ops/views/Sales/Other") with { TenantId = tenant.Id }, k.Owner.Id);
        Assert.Null(second.Warning);
    }

    [SqlFact]
    public async Task A_server_publish_refuses_a_power_bi_tenant_a_switched_off_tenant_and_a_view_on_the_wrong_site()
    {
        await using var k = await ArrangeAsync();
        var powerBi = new BiTenant { Name = Unique("PBI "), Platform = BiPlatform.PowerBi };
        var off = await TenantAsync(k.Db, "", false); await k.Db.BiTenants.Where(t => t.Id == off.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.IsActive, false));
        var ops = await TenantAsync(k.Db, "ops", false);
        k.Db.BiTenants.Add(powerBi);
        await k.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Public(k, url: "https://tableau.example.com/views/A/B") with { TenantId = powerBi.Id }, k.Owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Public(k, url: "https://tableau.example.com/views/A/B") with { TenantId = off.Id }, k.Owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Public(k, url: "https://tableau.example.com/views/A/B") with { TenantId = ops.Id }, k.Owner.Id)); // no site in the address
        await Assert.ThrowsAsync<RuleException>(() => k.Publisher.PublishAsync(Public(k, url: "https://tableau.example.com/views/A/B") with { TenantId = 99999999 }, k.Owner.Id));
    }

    [SqlFact]
    public async Task A_server_view_is_opened_with_a_connected_app_token_for_the_viewers_tableau_name_and_needs_that_name()
    {
        await using var k = await ArrangeAsync(memberTableauName: "jane.doe");
        var tenant = await TenantAsync(k.Db, "", true, k);
        var d = await k.Publisher.PublishAsync(Public(k, DataClassification.Internal, "https://tableau.example.com/views/Sales/Overview") with { TenantId = tenant.Id }, k.Owner.Id);
        await AddMemberAsync(k.Db, d.DashboardId, k.Member.Id);

        var info = await k.Embed.GetEmbedAsync(d.DashboardId, k.Member.Id, default);
        Assert.Equal("https://tableau.example.com/views/Sales/Overview", info.EmbedUrl);
        Assert.Equal("https://tableau.example.com/javascripts/api/tableau.embedding.3.latest.min.js", info.TableauScriptUrl);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(info.Token);
        Assert.Equal("jane.doe", jwt.Subject);

        await k.Db.Users.Where(u => u.Id == k.Member.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TableauUserName, (string?)null));
        var missing = await Assert.ThrowsAsync<EmbedException>(() => k.Embed.GetEmbedAsync(d.DashboardId, k.Member.Id, default));
        Assert.Equal("tableau-user-missing", missing.Code);
        Assert.Equal(1, await k.Db.DashboardViews.CountAsync(v => v.DashboardId == d.DashboardId));      // the failed open was not a view
    }

    [SqlFact]
    public async Task Changing_the_view_is_audited_and_a_stored_address_that_no_longer_fits_gives_a_clear_message()
    {
        await using var k = await ArrangeAsync(memberTableauName: "jane.doe");
        var tenant = await TenantAsync(k.Db, "", true, k);
        var d = await k.Publisher.PublishAsync(Public(k, DataClassification.Internal, "https://tableau.example.com/views/Sales/Overview") with { TenantId = tenant.Id }, k.Owner.Id);
        await AddMemberAsync(k.Db, d.DashboardId, k.Member.Id);

        await k.Master.SetTableauViewAsync(d.DashboardId, tenant.Id, "https://tableau.example.com/#/views/Sales/Detail", k.Admin.Id);
        Assert.Equal("https://tableau.example.com/views/Sales/Detail", (await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.DashboardId)).TableauViewUrl);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.Action == "dashboard.tableau-view-changed" && a.DashboardId == d.DashboardId));
        await Assert.ThrowsAsync<RuleException>(() => k.Master.SetTableauViewAsync(d.DashboardId, tenant.Id, "https://elsewhere.example.com/views/A/B", k.Admin.Id));

        // The tenant's server is moved afterwards: the stored address no longer matches it.
        await k.Db.BiTenants.Where(t => t.Id == tenant.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ServerUrl, "https://new-tableau.example.com"));
        k.Db.ChangeTracker.Clear();
        var broken = await Assert.ThrowsAsync<EmbedException>(() => k.Embed.GetEmbedAsync(d.DashboardId, k.Member.Id, default));
        Assert.Equal("not-configured", broken.Code);
        Assert.Contains("has to be on the tenant", broken.Message);
    }

    [SqlFact]
    public async Task Only_tableau_dashboards_have_a_view_to_change()
    {
        await using var k = await ArrangeAsync();
        var clock = TimeProvider.System;
        var pbi = await new DashboardFactory(k.Db, new OwnershipService(k.Db, clock), new AuditWriter(k.Db, new Ctx(), clock), clock).CreateAsync(new Dashboard
        {
            Code = "PB", Name = Unique("Pbi "), Type = DashboardType.PowerBi, Status = DashboardStatus.Active, PrimaryOwnerId = k.Owner.Id,
        }, null, k.Owner.Id);
        await Assert.ThrowsAsync<RuleException>(() => k.Master.SetTableauViewAsync(pbi.Id, null, "https://public.tableau.com/views/A/B", k.Admin.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => k.Master.SetTableauViewAsync(99999999, null, "https://public.tableau.com/views/A/B", k.Admin.Id));
    }
}
