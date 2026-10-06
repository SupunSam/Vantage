using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;

namespace Vantage.Tests;

/// <summary>Modify Dashboard (new .pbix over the live report), restore and the last-3-versions rule.</summary>
public class DashboardVersionTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
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

    /// <summary>Imports API stub: every POST starts a new import; GET returns the state the test sets.</summary>
    private sealed class StubPowerBi : HttpMessageHandler
    {
        public readonly Guid ReportId = Guid.NewGuid();
        public readonly Guid DatasetId = Guid.NewGuid();
        public string State = "Publishing";
        public bool ModelHasRls;
        public string? LastName;
        public int Imports;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Post && path.Contains("/imports?"))
            {
                Imports++;
                LastName = Uri.UnescapeDataString(path.Split("datasetDisplayName=")[1].Split('&')[0]);
                return Json($$$"""{"id":"{{{Guid.NewGuid()}}}"}""");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/imports/"))
                return State switch
                {
                    "Succeeded" => Json($$$"""{"id":"{{{Guid.NewGuid()}}}","importState":"Succeeded","reports":[{"id":"{{{ReportId}}}"}],"datasets":[{"id":"{{{DatasetId}}}"}]}"""),
                    "Failed" => Json("""{"importState":"Failed","error":{"code":"PackageNotValid","details":"Bad file."}}"""),
                    _ => Json("""{"importState":"Publishing"}"""),
                };
            if (path.Contains($"/datasets/{DatasetId}"))
                return Json($$$"""{"id":"{{{DatasetId}}}","name":"D","isEffectiveIdentityRequired":{{{(ModelHasRls ? "true" : "false")}}},"isEffectiveIdentityRolesRequired":{{{(ModelHasRls ? "true" : "false")}}}}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed record Kit(AppDbContext Db, DashboardVersionService Versions, StubPowerBi Stub, Dashboard Dashboard, User Owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Kit> ArrangeAsync()
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var stub = new StubPowerBi();
        var client = new PowerBiClient(new HttpClient(stub), new Tokens(), NullLogger<PowerBiClient>.Instance);
        var files = new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));
        var versions = new DashboardVersionService(db, client, files, audit, new NotificationService(db, clock), clock, NullLogger<DashboardVersionService>.Instance);

        var owner = new User { Email = $"{Guid.NewGuid():N}@rrd.com", UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        var tenant = new BiTenant { Name = "T" + Guid.NewGuid().ToString("N")[..8], Platform = BiPlatform.PowerBi, AzureTenantId = "t", ClientId = "c", SecretName = "s", CreatedAtUtc = DateTime.UtcNow,
            Workspaces = [new PowerBiWorkspace { Name = "WS", WorkspaceId = Guid.NewGuid() }] };
        db.AddRange(owner, tenant);
        await db.SaveChangesAsync();
        var factory = new DashboardFactory(db, new OwnershipService(db, clock), audit, clock);
        var d = await factory.CreateAsync(new Dashboard
        {
            Code = "SALES", Name = "Sales " + Guid.NewGuid().ToString("N")[..6], Type = BiType.PowerBi, Status = DashboardStatus.Active,
            TenantId = tenant.Id, WorkspaceId = tenant.Workspaces[0].Id, PrimaryOwnerId = owner.Id, PowerBiReportId = Guid.NewGuid(),
        }, null, owner.Id);
        // Version 1, as publishing leaves it.
        var key = $"dashboards/{d.Id}/v1/Sales.pbix";
        await files.SaveAsync(key, new MemoryStream([1, 2, 3]));
        db.DashboardVersions.Add(new DashboardVersion { DashboardId = d.Id, VersionNumber = 1, FileKey = key, FileName = "Sales.pbix", SizeBytes = 3, IsCurrent = true, UploadedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new Kit(db, versions, stub, d, owner);
    }

    private async Task<List<DashboardVersion>> VersionsAsync(Kit k) =>
        await k.Db.DashboardVersions.AsNoTracking().Where(v => v.DashboardId == k.Dashboard.Id).OrderBy(v => v.VersionNumber).ToListAsync();

    [SqlFact]
    public async Task Modify_imports_over_the_same_report_and_makes_the_new_file_current_when_done()
    {
        await using var k = await ArrangeAsync();
        k.Stub.ModelHasRls = true;

        var started = await k.Versions.ReplaceAsync(k.Dashboard.Id, new MemoryStream([9, 9]), "Sales v2.pbix", k.Owner.Id);
        Assert.Equal("Replacing", started.State);
        Assert.Equal($"SALES-{k.Dashboard.Id}.pbix", k.Stub.LastName); // same dataset name, so Power BI overwrites it
        Assert.Equal([true, false], (await VersionsAsync(k)).Select(v => v.IsCurrent)); // old file stays live meanwhile
        await Assert.ThrowsAsync<RuleException>(() => k.Versions.ReplaceAsync(k.Dashboard.Id, new MemoryStream([1]), "x.pbix", k.Owner.Id));

        k.Stub.State = "Succeeded";
        var done = await k.Versions.GetStatusAsync(k.Dashboard.Id);

        Assert.Equal("Succeeded", done.State);
        Assert.Equal([false, true], (await VersionsAsync(k)).Select(v => v.IsCurrent));
        var d = await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == k.Dashboard.Id);
        Assert.Equal((k.Stub.ReportId, DashboardStatus.Active, true, (Guid?)null), (d.PowerBiReportId!.Value, d.Status, d.RlsEnabled, d.ReplaceImportId));
        Assert.True(await k.Db.Notifications.AnyAsync(n => n.UserId == k.Owner.Id && n.Type == "dashboard.replaced"));
    }

    [SqlFact]
    public async Task A_failed_import_keeps_the_previous_version_live_and_drops_the_new_file()
    {
        await using var k = await ArrangeAsync();
        await k.Versions.ReplaceAsync(k.Dashboard.Id, new MemoryStream([9]), "broken.pbix", k.Owner.Id);
        k.Stub.State = "Failed";

        var status = await k.Versions.GetStatusAsync(k.Dashboard.Id);

        Assert.Equal("Failed", status.State);
        Assert.Contains("previous version is still live", status.Message);
        var versions = await VersionsAsync(k);
        Assert.Equal([1], versions.Select(v => v.VersionNumber));
        Assert.True(versions[0].IsCurrent);
    }

    [SqlFact]
    public async Task Only_the_last_three_versions_are_kept_and_an_old_one_can_be_restored()
    {
        await using var k = await ArrangeAsync();
        k.Stub.State = "Succeeded";
        for (var i = 2; i <= 4; i++)
        {
            await k.Versions.ReplaceAsync(k.Dashboard.Id, new MemoryStream([(byte)i]), $"v{i}.pbix", k.Owner.Id);
            await k.Versions.GetStatusAsync(k.Dashboard.Id);
        }
        Assert.Equal([2, 3, 4], (await VersionsAsync(k)).Select(v => v.VersionNumber));

        await k.Versions.RestoreAsync(k.Dashboard.Id, 2, k.Owner.Id);
        await k.Versions.GetStatusAsync(k.Dashboard.Id);

        var versions = await VersionsAsync(k);
        Assert.Equal([3, 4, 5], versions.Select(v => v.VersionNumber));
        Assert.True(versions.Single(v => v.VersionNumber == 5).IsCurrent);
        Assert.Equal("Restored from version 2", versions.Single(v => v.VersionNumber == 5).ScanReport);
        var (content, name) = await k.Versions.OpenVersionAsync(k.Dashboard.Id, 5);
        await using (content) Assert.Equal(2, content.ReadByte()); // the restored file is version 2's
        Assert.Equal("v2.pbix", name);
        await Assert.ThrowsAsync<RuleException>(() => k.Versions.RestoreAsync(k.Dashboard.Id, 5, k.Owner.Id));
    }
}
