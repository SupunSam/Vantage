using System.Buffers.Binary;
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

/// <summary>Categories, Dashboards Master edits, thumbnails, the RLS group rule and notifications.</summary>
public class CatalogueTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
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

    /// <summary>Answers GET report and GET dataset; the dataset's RLS flag is whatever the test sets.</summary>
    private sealed class FakePowerBi : HttpMessageHandler
    {
        public readonly Guid ReportId = Guid.NewGuid();
        public readonly Guid DatasetId = Guid.NewGuid();
        public bool ModelHasRls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            string? body = null;
            if (path.Contains($"/reports/{ReportId}"))
                body = $$$"""{"id":"{{{ReportId}}}","name":"R","embedUrl":"https://app.powerbi.com/x","datasetId":"{{{DatasetId}}}"}""";
            else if (path.Contains($"/datasets/{DatasetId}"))
                body = $$$"""{"id":"{{{DatasetId}}}","name":"D","isEffectiveIdentityRequired":{{{(ModelHasRls ? "true" : "false")}}},"isEffectiveIdentityRolesRequired":{{{(ModelHasRls ? "true" : "false")}}}}""";
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private sealed record Kit(AppDbContext Db, CategoryService Categories, DashboardMasterService Master, GroupService Groups, FakePowerBi PowerBi) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Kit Arrange()
    {
        var db = fx.CreateContext();
        var clock = TimeProvider.System;
        var audit = new AuditWriter(db, new Ctx(), clock);
        var fake = new FakePowerBi();
        var client = new PowerBiClient(new HttpClient(fake), new Tokens(), NullLogger<PowerBiClient>.Instance);
        var files = new LocalFileStore(Path.Combine(Path.GetTempPath(), "vantage-tests", Guid.NewGuid().ToString("N")));
        return new Kit(db, new CategoryService(db, audit, clock), new DashboardMasterService(db, new OwnershipService(db, clock), client, files, audit, clock),
            new GroupService(db, audit, clock), fake);
    }

    private static async Task<User> UserAsync(AppDbContext db)
    {
        var u = new User { Email = Unique("u") + "@rrd.com", DisplayName = Unique("User "), UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static async Task<Dashboard> DashboardAsync(Kit k, User owner, bool rls, int? categoryId = null, bool linkedToFake = false)
    {
        var tenant = new BiTenant { Name = Unique("T"), Platform = BiPlatform.PowerBi, CreatedAtUtc = DateTime.UtcNow, Workspaces = [new PowerBiWorkspace { Name = "WS", WorkspaceId = Guid.NewGuid() }] };
        k.Db.BiTenants.Add(tenant);
        await k.Db.SaveChangesAsync();
        var audit = new AuditWriter(k.Db, new Ctx(), TimeProvider.System);
        var factory = new DashboardFactory(k.Db, new OwnershipService(k.Db, TimeProvider.System), audit, TimeProvider.System);
        return await factory.CreateAsync(new Dashboard
        {
            Code = "D", Name = Unique("Dash "), Type = BiType.PowerBi, Status = DashboardStatus.Active, RlsEnabled = rls,
            PrimaryOwnerId = owner.Id, CategoryId = categoryId, TenantId = tenant.Id, WorkspaceId = tenant.Workspaces[0].Id, PowerBiReportId = linkedToFake ? k.PowerBi.ReportId : Guid.NewGuid(),
        }, rls ? "Region_All" : null, owner.Id);
    }

    // ------------------------------------------------------------ Categories

    [SqlFact]
    public async Task Categories_form_a_three_level_tree_with_full_paths()
    {
        await using var k = Arrange();
        var top = await k.Categories.CreateAsync(Unique("Finance"), null);
        var mid = await k.Categories.CreateAsync("Payroll", top.Id);
        var low = await k.Categories.CreateAsync("Monthly", mid.Id);

        Assert.Equal([1, 2, 3], new[] { top.Level, mid.Level, low.Level });
        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Categories.CreateAsync("Too deep", low.Id));
        Assert.Contains("deepest", ex.Message);

        var paths = await k.Categories.PathsAsync();
        Assert.Equal($"{top.Name} / Payroll / Monthly", paths[low.Id]);
    }

    [SqlFact]
    public async Task Category_names_are_unique_within_their_parent_only()
    {
        await using var k = Arrange();
        var a = await k.Categories.CreateAsync(Unique("Ops"), null);
        var b = await k.Categories.CreateAsync(Unique("Sales"), null);
        await k.Categories.CreateAsync("Reports", a.Id);
        await k.Categories.CreateAsync("Reports", b.Id); // same name, different parent: fine

        await Assert.ThrowsAsync<RuleException>(() => k.Categories.CreateAsync("reports", a.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Categories.CreateAsync(a.Name, null));
        await Assert.ThrowsAsync<RuleException>(() => k.Categories.CreateAsync("A/B", null));
    }

    [SqlFact]
    public async Task A_category_holding_dashboards_or_children_cannot_be_deleted()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var top = await k.Categories.CreateAsync(Unique("HR"), null);
        var child = await k.Categories.CreateAsync("Leave", top.Id);
        await DashboardAsync(k, owner, rls: false, child.Id);

        Assert.Contains("sub-categor", (await Assert.ThrowsAsync<RuleException>(() => k.Categories.DeleteAsync(top.Id))).Message);
        Assert.Contains("1 dashboard", (await Assert.ThrowsAsync<RuleException>(() => k.Categories.DeleteAsync(child.Id))).Message);

        var empty = await k.Categories.CreateAsync("Empty", top.Id);
        await k.Categories.DeleteAsync(empty.Id);
        Assert.False(await k.Db.Categories.AnyAsync(c => c.Id == empty.Id));
    }

    [SqlFact]
    public async Task Moving_a_category_swaps_it_with_its_neighbour()
    {
        await using var k = Arrange();
        var top = await k.Categories.CreateAsync(Unique("Order"), null);
        var first = await k.Categories.CreateAsync("First", top.Id);
        var second = await k.Categories.CreateAsync("Second", top.Id);

        await k.Categories.MoveAsync(second.Id, -1);

        var order = (await k.Categories.ListAsync()).Where(c => c.ParentId == top.Id).Select(c => c.Name).ToList();
        Assert.Equal(["Second", "First"], order);
    }

    // ------------------------------------------------------------ Dashboards Master

    [SqlFact]
    public async Task Editing_details_renames_recategorises_tags_and_hands_ownership_over()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var newOwner = await UserAsync(k.Db);
        var cat = await k.Categories.CreateAsync(Unique("Cat"), null);
        var d = await DashboardAsync(k, owner, rls: false, cat.Id);
        var newName = Unique("Renamed ");

        await k.Master.UpdateAsync(d.Id, new DashboardDetailsInput(newName, "About it", cat.Id, newOwner.Id, owner.Id,
            ["Sales", "sales", "Q3"], Audience.Client, DataClassification.Confidential, "Finance", "Monthly"), owner.Id);

        k.Db.ChangeTracker.Clear();
        var saved = await k.Db.Dashboards.Include(x => x.Tags).SingleAsync(x => x.Id == d.Id);
        Assert.Equal(newName, saved.Name);
        Assert.Equal(newOwner.Id, saved.PrimaryOwnerId);
        Assert.Equal(owner.Id, saved.BackupOwnerId);
        Assert.Equal(["Q3", "Sales"], saved.Tags.Select(t => t.Tag).OrderBy(t => t));
        Assert.Equal(Audience.Client, saved.Audience);
        Assert.Equal("Monthly", saved.SharePointSubFolder);
        // The new owner is put in the default group and gets the Dashboard Owner role.
        Assert.True(await k.Db.GroupMembers.AnyAsync(m => m.DashboardId == d.Id && m.UserId == newOwner.Id && m.RemovedAtUtc == null && m.Group.IsDefault));
        Assert.True(await k.Db.UserRoles.AnyAsync(r => r.UserId == newOwner.Id && r.Role.Name == SystemRoles.DashboardOwner));
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.DashboardId == d.Id && a.Action == "dashboard.updated"));
    }

    [SqlFact]
    public async Task Edits_check_names_tags_and_category()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var cat = await k.Categories.CreateAsync(Unique("Cat"), null);
        var other = await DashboardAsync(k, owner, rls: false, cat.Id);
        var d = await DashboardAsync(k, owner, rls: false, cat.Id);
        DashboardDetailsInput Input(string name, int? category, params string[] tags) =>
            new(name, null, category, owner.Id, null, tags, Audience.Internal, DataClassification.Internal, null, null);

        await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.Id, Input(other.Name, cat.Id), owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.Id, Input(d.Name, null), owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.Id, Input(d.Name, cat.Id, "a", "b", "c", "d", "e", "f", "g", "h", "i"), owner.Id));
        Assert.Contains("letters and digits", (await Assert.ThrowsAsync<RuleException>(() => k.Master.UpdateAsync(d.Id, Input(d.Name, cat.Id, "Q3-2026"), owner.Id))).Message);
    }

    [SqlFact]
    public async Task Check_rls_reads_the_flag_from_the_power_bi_model()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner, rls: false, linkedToFake: true);
        k.PowerBi.ModelHasRls = true;

        var (rls, changed) = await k.Master.SyncRlsAsync(d.Id);

        Assert.True(rls);
        Assert.True(changed);
        Assert.True(await k.Db.AuditLogs.AnyAsync(a => a.DashboardId == d.Id && a.Action == "dashboard.rls-corrected"));
    }

    [SqlFact]
    public async Task A_valid_thumbnail_is_stored_under_a_new_key_each_time()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner, rls: false);

        var first = await k.Master.SetThumbnailAsync(d.Id, Png(1280, 720));
        var second = await k.Master.SetThumbnailAsync(d.Id, Png(640, 360));

        Assert.NotEqual(first, second);
        Assert.EndsWith(".png", second);
        await k.Master.RemoveThumbnailAsync(d.Id);
        Assert.Null((await k.Db.Dashboards.AsNoTracking().SingleAsync(x => x.Id == d.Id)).ThumbnailKey);
    }

    // ------------------------------------------------------------ Groups and RLS

    [SqlFact]
    public async Task A_dashboard_without_rls_keeps_only_its_default_group()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner, rls: false);

        var ex = await Assert.ThrowsAsync<RuleException>(() => k.Groups.AddAsync(d.Id, Unique("G"), "Region_North", owner.Id));
        Assert.Equal(GroupService.NoRlsMessage, ex.Message);

        // The RLS value can still be set; it's simply not sent in the embed token.
        var defaultGroup = await k.Db.DashboardGroups.SingleAsync(g => g.DashboardId == d.Id && g.IsDefault);
        var saved = await k.Groups.SetRlsAsync(d.Id, defaultGroup.Id, " Region_All ");
        Assert.Equal("Region_All", saved.RlsValue);
    }

    [SqlFact]
    public async Task A_dashboard_with_rls_can_have_many_groups_each_with_an_rls_value()
    {
        await using var k = Arrange();
        var owner = await UserAsync(k.Db);
        var d = await DashboardAsync(k, owner, rls: true);

        var north = await k.Groups.AddAsync(d.Id, Unique("North"), "Region_North ; Region_East", owner.Id);
        Assert.Equal("Region_North,Region_East", north.RlsValue);

        await Assert.ThrowsAsync<RuleException>(() => k.Groups.AddAsync(d.Id, Unique("South"), null, owner.Id));
        await Assert.ThrowsAsync<RuleException>(() => k.Groups.SetRlsAsync(d.Id, north.Id, ""));
    }

    // ------------------------------------------------------------ Notifications

    [SqlFact]
    public async Task Notifications_are_listed_newest_first_and_can_be_marked_read()
    {
        await using var k = Arrange();
        var user = await UserAsync(k.Db);
        var svc = new NotificationService(k.Db, TimeProvider.System);
        svc.Notify([user.Id, user.Id], "test", "First");
        await k.Db.SaveChangesAsync();
        svc.Notify([user.Id], "test", "Second");
        await k.Db.SaveChangesAsync();

        var (items, unread) = await svc.ListAsync(user.Id);
        Assert.Equal(2, unread); // the duplicate recipient got one notification
        Assert.Equal("Second", items[0].Title);

        await svc.MarkReadAsync(user.Id, items[0].Id);
        Assert.Equal(1, (await svc.ListAsync(user.Id)).Unread);
        await svc.MarkReadAsync(user.Id, null);
        Assert.Equal(0, (await svc.ListAsync(user.Id)).Unread);
    }

    // ------------------------------------------------------------ Thumbnail rules (no database)

    [Fact]
    public void Thumbnails_must_be_png_or_jpg_16_by_9_and_at_least_640_by_360()
    {
        Assert.Equal("image/png", Thumbnails.Validate(Png(640, 360)).ContentType);
        Assert.Equal((1920, 1080), (Thumbnails.Validate(Jpeg(1920, 1080)).Width, Thumbnails.Validate(Jpeg(1920, 1080)).Height));
        Assert.Contains("16:9", Assert.Throws<RuleException>(() => Thumbnails.Validate(Png(800, 800))).Message);
        Assert.Contains("at least", Assert.Throws<RuleException>(() => Thumbnails.Validate(Png(320, 180))).Message);
        Assert.Contains("PNG or JPG", Assert.Throws<RuleException>(() => Thumbnails.Validate("GIF89a-not-an-image"u8)).Message);
        Assert.Contains("1 MB", Assert.Throws<RuleException>(() => Thumbnails.Validate(new byte[Rules.ThumbnailMaxBytes + 1])).Message);
    }

    [Fact]
    public void Tags_are_split_trimmed_and_deduplicated_ignoring_case()
    {
        Assert.Equal(["Sales", "q3", "APAC"], Rules.NormalizeTags(["Sales, q3 sales", " Q3;APAC"]));
    }

    /// <summary>The smallest byte layout the probe reads: PNG signature plus the IHDR width and height.</summary>
    private static byte[] Png(int width, int height)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    /// <summary>SOI, an APP0 segment, then a baseline SOF0 frame header with the size.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        b.AddRange(new byte[14]);
        b.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        b.AddRange(new byte[12]);
        return [.. b];
    }
}
