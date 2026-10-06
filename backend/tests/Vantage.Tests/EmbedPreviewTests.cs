using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Super Admins preview dashboards as a group would see them, without being in the group and without it counting as usage.</summary>
public class EmbedPreviewTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
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

    /// <summary>A stand-in for the Power BI REST API that remembers the GenerateToken request.</summary>
    private sealed class FakePowerBi(Guid datasetId) : HttpMessageHandler
    {
        public JsonElement? TokenRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("GenerateToken"))
            {
                TokenRequest = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                json = """{"token":"abc","expiration":"2030-01-01T10:00:00Z"}""";
            }
            else if (path.Contains("/reports/"))
                json = $$"""{"id":"{{Guid.NewGuid()}}","name":"R","embedUrl":"https://app.powerbi.com/reportEmbed","datasetId":"{{datasetId}}"}""";
            else
                json = $$"""{"id":"{{datasetId}}","name":"D","isEffectiveIdentityRequired":true,"isEffectiveIdentityRolesRequired":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, EmbedService Embed, FakePowerBi Fake, User Admin, Dashboard Dashboard, DashboardGroup North) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync(DashboardStatus status = DashboardStatus.Active)
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var owner = new User { Email = Unique("o") + "@rrd.com", DisplayName = Unique("Owner "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var admin = new User { Email = Unique("a") + "@rrd.com", DisplayName = Unique("Admin "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.AddRange(owner, admin);
        var tenant = new BiTenant { Name = Unique("T"), Platform = BiPlatform.PowerBi };
        db.BiTenants.Add(tenant);
        await db.SaveChangesAsync();
        var workspace = new PowerBiWorkspace { TenantId = tenant.Id, Name = Unique("W"), WorkspaceId = Guid.NewGuid() };
        db.PowerBiWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        var factory = new DashboardFactory(db, new OwnershipService(db, clock), audit, clock);
        var d = await factory.CreateAsync(new Dashboard
        {
            Code = "PV", Name = Unique("Dash "), Type = BiType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = true, PrimaryOwnerId = owner.Id,
            TenantId = tenant.Id, WorkspaceId = workspace.Id, PowerBiReportId = Guid.NewGuid(),
        }, "Region_All", owner.Id);
        if (status != DashboardStatus.Active) { d.Status = status; await db.SaveChangesAsync(); }
        var north = (await db.DashboardGroups.AddAsync(new DashboardGroup { DashboardId = d.Id, Name = Unique("N"), RlsValue = "Region_North", Status = GroupStatus.Active, CreatedAtUtc = DateTime.UtcNow })).Entity;
        await db.SaveChangesAsync();

        var fake = new FakePowerBi(Guid.NewGuid());
        var powerBi = new PowerBiClient(new HttpClient(fake), new Tokens(), NullLogger<PowerBiClient>.Instance);
        return new Kit(db, new EmbedService(db, powerBi, new TableauTokenService(null!, clock), GenAiTestKit.Service(db, audit, clock), audit, clock), fake, admin, d, north);
    }

    [SqlFact]
    public async Task A_preview_needs_no_membership_uses_the_groups_role_and_is_not_a_view()
    {
        await using var k = await ArrangeAsync();

        var info = await k.Embed.PreviewAsync(k.Dashboard.Id, k.North.Id, k.Admin.Id, default);

        Assert.Equal("abc", info.Token);
        var identity = k.Fake.TokenRequest!.Value.GetProperty("identities")[0];
        Assert.Equal(k.Admin.Email, identity.GetProperty("username").GetString());          // the admin's own email
        Assert.Equal("Region_North", identity.GetProperty("roles")[0].GetString());         // the chosen group's role
        Assert.False(await k.Db.GroupMembers.AnyAsync(m => m.UserId == k.Admin.Id));        // not added to anything
        Assert.False(await k.Db.DashboardViews.AnyAsync(v => v.UserId == k.Admin.Id));      // not counted as usage
        Assert.Null((await k.Db.Dashboards.AsNoTracking().SingleAsync(d => d.Id == k.Dashboard.Id)).LastViewedAtUtc);
        Assert.Equal(1, await k.Db.AuditLogs.CountAsync(a => a.Action == "dashboard.previewed" && a.EntityId == k.Dashboard.Id.ToString()));
    }

    [SqlFact]
    public async Task Opening_a_dashboard_to_consume_it_still_needs_a_group()
    {
        await using var k = await ArrangeAsync();
        await Assert.ThrowsAsync<NoAccessException>(() => k.Embed.GetEmbedAsync(k.Dashboard.Id, k.Admin.Id, default));
    }

    [SqlFact]
    public async Task A_preview_refuses_foreign_groups_groups_without_a_role_and_dashboards_that_are_not_active()
    {
        await using var k = await ArrangeAsync();
        var other = await ArrangeAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => k.Embed.PreviewAsync(k.Dashboard.Id, other.North.Id, k.Admin.Id, default));   // another dashboard's group

        k.North.RlsValue = null;
        await k.Db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Embed.PreviewAsync(k.Dashboard.Id, k.North.Id, k.Admin.Id, default));
        Assert.Contains("RLS value", ex.Message);

        await using var inactive = await ArrangeAsync(DashboardStatus.Inactive);
        await Assert.ThrowsAsync<RuleException>(() => inactive.Embed.PreviewAsync(inactive.Dashboard.Id, inactive.North.Id, inactive.Admin.Id, default));
        Assert.False(await k.Db.AuditLogs.AnyAsync(a => a.Action == "dashboard.previewed" && a.EntityId == k.Dashboard.Id.ToString()));
        await other.DisposeAsync();
    }
}
