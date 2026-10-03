using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>Publishing a .pbix end to end against SQL Server, with the Power BI REST API replaced by a stub.</summary>
public class PublishingTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class FakeTokens : IPowerBiTokenProvider
    {
        public bool Fail;
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) =>
            Fail ? throw new EmbedException("secret-missing", "Client secret is not set.") : Task.FromResult("test-token");
    }

    private sealed class TestContext : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    /// <summary>Answers the Imports API: POST returns an import ID; GET returns whatever state the test sets.</summary>
    private sealed class StubPowerBi : HttpMessageHandler
    {
        public static readonly Guid ImportId = Guid.NewGuid();
        public readonly Guid ReportId = Guid.NewGuid();
        public readonly Guid DatasetId = Guid.NewGuid();
        public string State = "Publishing";
        public bool ModelHasRls = true;
        public string? LastUploadName;
        public long LastUploadBytes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Post && path.Contains("/imports?"))
            {
                LastUploadName = Uri.UnescapeDataString(path.Split("datasetDisplayName=")[1].Split('&')[0]);
                LastUploadBytes = (await request.Content!.ReadAsByteArrayAsync(ct)).Length;
                return Json(HttpStatusCode.Accepted, $$$"""{"id":"{{{ImportId}}}"}""");
            }
            if (request.Method == HttpMethod.Get && path.Contains($"/imports/{ImportId}"))
            {
                return State switch
                {
                    "Succeeded" => Json(HttpStatusCode.OK, $$$"""{"id":"{{{ImportId}}}","importState":"Succeeded","reports":[{"id":"{{{ReportId}}}"}],"datasets":[{"id":"{{{DatasetId}}}"}]}"""),
                    "Failed" => Json(HttpStatusCode.OK, $$$"""{"id":"{{{ImportId}}}","importState":"Failed","error":{"code":"PackageNotValid","details":"The file is corrupt."}}"""),
                    _ => Json(HttpStatusCode.OK, $$$"""{"id":"{{{ImportId}}}","importState":"Publishing"}"""),
                };
            }
            if (request.Method == HttpMethod.Get && path.Contains($"/datasets/{DatasetId}"))
                return Json(HttpStatusCode.OK, $$$"""{"id":"{{{DatasetId}}}","name":"Sales","isEffectiveIdentityRequired":{{{(ModelHasRls ? "true" : "false")}}},"isEffectiveIdentityRolesRequired":{{{(ModelHasRls ? "true" : "false")}}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private async Task<(PublishingService Service, AppDbContext Db, StubPowerBi Stub, User Owner, PowerBiWorkspace Workspace)> ArrangeAsync(FakeTokens? tokens = null)
    {
        var db = fx.CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var stub = new StubPowerBi();
        var client = new PowerBiClient(new HttpClient(stub), tokens ?? new FakeTokens(), NullLogger<PowerBiClient>.Instance);
        var audit = new AuditWriter(db, new TestContext(), clock);
        var factory = new DashboardFactory(db, new OwnershipService(db, clock), audit, clock);
        var files = new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));

        var owner = new User { Email = $"{Guid.NewGuid():N}@rrd.com", UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var tenant = new BiTenant
        {
            Name = "T" + Guid.NewGuid().ToString("N")[..8], Platform = BiPlatform.PowerBi, AzureTenantId = "t", ClientId = "c", SecretName = "s",
            CreatedAtUtc = DateTime.UtcNow, Workspaces = [new PowerBiWorkspace { Name = "WS", WorkspaceId = Guid.NewGuid() }],
        };
        var category = new Category { Name = "Cat " + Guid.NewGuid().ToString("N")[..8], Level = 1, CreatedAtUtc = DateTime.UtcNow };
        db.AddRange(owner, tenant, category);
        await db.SaveChangesAsync();
        _categoryId = category.Id;

        var ownership = new OwnershipService(db, clock);
        var master = new DashboardMasterService(db, ownership, client, files, audit, clock);
        var service = new PublishingService(db, factory, master, client, files, audit, new NotificationService(db, clock), clock, NullLogger<PublishingService>.Instance);
        return (service, db, stub, owner, tenant.Workspaces[0]);
    }

    private int _categoryId;

    private PublishPowerBiRequest Request(int workspaceId, int ownerId, bool rls = true) => new(
        Name: "Sales " + Guid.NewGuid().ToString("N")[..6], Code: "SALES", Description: null, WorkspaceId: workspaceId,
        PrimaryOwnerId: ownerId, BackupOwnerId: null, RlsEnabled: rls, DefaultGroupRlsValue: rls ? "Region_All" : null,
        CategoryId: _categoryId, FileName: "Sales.pbix");

    [SqlFact]
    public async Task Upload_imports_into_the_workspace_and_goes_live_with_the_published_report_id()
    {
        var (service, db, stub, owner, ws) = await ArrangeAsync();
        await using var _ = db;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 4096));

        var started = await service.PublishPowerBiAsync(Request(ws.Id, owner.Id), new MemoryStream(bytes), owner.Id, default);

        Assert.Equal("Publishing", started.Status);
        Assert.Equal($"SALES-{started.DashboardId}.pbix", stub.LastUploadName);
        Assert.True(stub.LastUploadBytes > bytes.Length); // multipart body includes the file
        Assert.Equal($"SALES-{started.DashboardId}-default", started.DefaultGroup);

        // Still importing: stays Publishing.
        Assert.Equal("Publishing", (await service.GetStatusAsync(started.DashboardId, default)).Status);

        stub.State = "Succeeded";
        var done = await service.GetStatusAsync(started.DashboardId, default);
        Assert.Equal("Active", done.Status);
        Assert.Equal(stub.ReportId, done.ReportId);

        var d = await db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == started.DashboardId);
        Assert.Equal(stub.DatasetId, d.PowerBiDatasetId);
        var version = await db.DashboardVersions.AsNoTracking().SingleAsync(v => v.DashboardId == d.Id);
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal(bytes.Length, version.SizeBytes);
        Assert.True(version.IsCurrent);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.DashboardId == d.Id && a.Action == "dashboard.published"));
        Assert.True(await db.GroupMembers.AnyAsync(m => m.DashboardId == d.Id && m.UserId == owner.Id && m.RemovedAtUtc == null));
    }

    [SqlFact]
    public async Task A_failed_import_marks_the_dashboard_failed_with_the_reason()
    {
        var (service, db, stub, owner, ws) = await ArrangeAsync();
        await using var _ = db;

        var started = await service.PublishPowerBiAsync(Request(ws.Id, owner.Id, rls: false), new MemoryStream([1, 2, 3]), owner.Id, default);
        stub.State = "Failed";
        var status = await service.GetStatusAsync(started.DashboardId, default);

        Assert.Equal("Failed", status.Status);
        Assert.Contains("PackageNotValid", status.Error);
    }

    [SqlFact]
    public async Task Rls_without_a_default_group_role_is_refused_before_anything_is_uploaded()
    {
        var (service, db, stub, owner, ws) = await ArrangeAsync();
        await using var _ = db;
        var req = Request(ws.Id, owner.Id) with { DefaultGroupRlsValue = null };

        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishPowerBiAsync(req, new MemoryStream([1]), owner.Id, default));
        Assert.Null(stub.LastUploadName);
    }

    [SqlFact]
    public async Task Publishing_again_over_a_failed_attempt_reuses_the_dashboard_as_version_2()
    {
        var (service, db, stub, owner, ws) = await ArrangeAsync();
        await using var _ = db;
        var req = Request(ws.Id, owner.Id, rls: false);

        var first = await service.PublishPowerBiAsync(req, new MemoryStream([1, 2, 3]), owner.Id, default);
        stub.State = "Failed";
        Assert.Equal("Failed", (await service.GetStatusAsync(first.DashboardId, default)).Status);

        stub.State = "Succeeded";
        var second = await service.PublishPowerBiAsync(req, new MemoryStream([4, 5, 6, 7]), owner.Id, default);
        Assert.Equal(first.DashboardId, second.DashboardId);
        Assert.Equal("Active", (await service.GetStatusAsync(second.DashboardId, default)).Status);

        var versions = await db.DashboardVersions.AsNoTracking().Where(v => v.DashboardId == first.DashboardId).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.VersionNumber));
        Assert.Equal([false, true], versions.Select(v => v.IsCurrent));
        Assert.Equal(1, await db.DashboardGroups.CountAsync(g => g.DashboardId == first.DashboardId && g.IsDefault));
    }

    [SqlFact]
    public async Task A_sign_in_failure_creates_no_dashboard()
    {
        var (service, db, _, owner, ws) = await ArrangeAsync(new FakeTokens { Fail = true });
        await using var __ = db;
        var req = Request(ws.Id, owner.Id, rls: false);

        await Assert.ThrowsAsync<EmbedException>(() => service.PublishPowerBiAsync(req, new MemoryStream([1]), owner.Id, default));
        Assert.False(await db.Dashboards.AnyAsync(d => d.Name == req.Name));
    }

    [SqlFact]
    public async Task The_rls_flag_follows_the_report_not_the_checkbox()
    {
        var (service, db, stub, owner, ws) = await ArrangeAsync();
        await using var _ = db;
        stub.ModelHasRls = true;

        // Published with the RLS box unticked, but the report has roles.
        var started = await service.PublishPowerBiAsync(Request(ws.Id, owner.Id, rls: false), new MemoryStream([1, 2]), owner.Id, default);
        stub.State = "Succeeded";
        var status = await service.GetStatusAsync(started.DashboardId, default);

        Assert.Equal("Active", status.Status);
        Assert.True((await db.Dashboards.AsNoTracking().SingleAsync(d => d.Id == started.DashboardId)).RlsEnabled);
        Assert.Contains("no RLS role", status.Warning);
    }
}
