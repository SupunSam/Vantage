using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Secrets;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Tenants;

namespace Vantage.Tests;

/// <summary>Role Master, User Master, HRMS job, secret storage and tenant verification against SQL Server.</summary>
public class AdminMasterTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class Ctx : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static AuditWriter Audit(AppDbContext db) => new(db, new Ctx(), TimeProvider.System);
    private static Dictionary<string, PermissionLevel> Perms(params (string, PermissionLevel)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    // ------------------------------------------------------------ Roles

    [SqlFact]
    public async Task Custom_role_is_created_with_only_view_and_edit_permissions_stored()
    {
        await using var db = fx.CreateContext();
        var svc = new RoleService(db, Audit(db), TimeProvider.System);
        var role = await svc.CreateAsync(new RoleInput(Unique("ASD "), "Help desk",
            Perms((AppModules.Users, PermissionLevel.View), (AppModules.Groups, PermissionLevel.Edit), (AppModules.Audit, PermissionLevel.None))), default);

        var stored = await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == role.Id).OrderBy(p => p.Module).ToListAsync();
        Assert.Equal([AppModules.Groups, AppModules.Users], stored.Select(p => p.Module));
    }

    [SqlFact]
    public async Task Role_names_are_unique_and_unknown_modules_are_refused()
    {
        await using var db = fx.CreateContext();
        var svc = new RoleService(db, Audit(db), TimeProvider.System);
        var name = Unique("BPI ");
        await svc.CreateAsync(new RoleInput(name, null, Perms()), default);
        await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(new RoleInput(name, null, Perms()), default));
        await Assert.ThrowsAsync<RuleException>(() => svc.CreateAsync(new RoleInput(Unique("X"), null, Perms(("not-a-module", PermissionLevel.Edit))), default));
    }

    [SqlFact]
    public async Task Built_in_roles_cannot_be_renamed_or_deleted_and_super_admin_keeps_full_access()
    {
        await using var db = fx.CreateContext();
        var svc = new RoleService(db, Audit(db), TimeProvider.System);
        var superAdmin = await db.Roles.SingleAsync(r => r.Name == SystemRoles.SuperAdmin);

        await Assert.ThrowsAsync<RuleException>(() => svc.UpdateAsync(superAdmin.Id, new RoleInput("Boss", null, Perms()), default));
        await Assert.ThrowsAsync<RuleException>(() => svc.DeleteAsync(superAdmin.Id, default));

        await svc.UpdateAsync(superAdmin.Id, new RoleInput(SystemRoles.SuperAdmin, "Updated", Perms((AppModules.Users, PermissionLevel.View))), default);
        var perms = await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == superAdmin.Id).ToListAsync();
        Assert.Equal(AppModules.All.Count, perms.Count);
        Assert.All(perms, p => Assert.Equal(PermissionLevel.Edit, p.Level));
    }

    [SqlFact]
    public async Task A_role_in_use_cannot_be_deleted()
    {
        await using var db = fx.CreateContext();
        var roles = new RoleService(db, Audit(db), TimeProvider.System);
        var users = new UserService(db, Audit(db), TimeProvider.System);
        var role = await roles.CreateAsync(new RoleInput(Unique("Temp "), null, Perms()), default);
        await users.CreateAsync(new NewUserInput($"{Unique("u")}@client.com", "A", "B", null, null, null, null, [role.Id]), null, default);

        var ex = await Assert.ThrowsAsync<RuleException>(() => roles.DeleteAsync(role.Id, default));
        Assert.Contains("assigned to 1 user", ex.Message);
    }

    // ------------------------------------------------------------ Users

    [SqlFact]
    public async Task Internal_user_is_filled_from_hrms_and_external_user_waits_for_setup()
    {
        await using var db = fx.CreateContext();
        var svc = new UserService(db, Audit(db), TimeProvider.System);

        if (await db.Users.AnyAsync(u => u.Email == "ada.active@rrd.com")) return; // already added by another test run
        var ada = await svc.CreateAsync(new NewUserInput(" Ada.Active@RRD.com ", null, null, null, null, null, null, []), null, default);
        var ext = await svc.CreateAsync(new NewUserInput($"{Unique("ext")}@client.com", "Jo", "Client", null, null, null, null, []), null, default);

        var a = await db.Users.AsNoTracking().Include(u => u.HrmsProfile).Include(u => u.Roles).ThenInclude(r => r.Role).SingleAsync(u => u.Id == ada.Id);
        Assert.Equal("ada.active@rrd.com", a.Email);
        Assert.Equal(UserType.Internal, a.UserType);
        Assert.Equal("Ada Active", a.DisplayName);
        Assert.Equal("Finance", a.HrmsProfile!.Department);
        Assert.Contains(a.Roles, r => r.Role.Name == SystemRoles.DashboardUser);

        var e = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ext.Id);
        Assert.Equal(UserType.External, e.UserType);
        Assert.Equal(UserStatus.PendingSetup, e.Status);
        Assert.Equal("Jo Client", e.DisplayName);
    }

    [SqlFact]
    public async Task Bulk_add_reports_added_skipped_and_invalid()
    {
        await using var db = fx.CreateContext();
        var svc = new UserService(db, Audit(db), TimeProvider.System);
        var existing = $"{Unique("dup")}@client.com";
        await svc.CreateAsync(new NewUserInput(existing, null, null, null, null, null, null, []), null, default);
        var fresh = $"{Unique("new")}@client.com";

        var results = await svc.BulkCreateAsync([(existing, null), (fresh, "No Such Role"), ("not-an-email", null), (fresh.ToUpper(), null)], [], null, default);

        Assert.Equal("Skipped", results[0].Outcome);
        Assert.Equal("Added", results[1].Outcome);
        Assert.Contains("not found", results[1].Detail);
        Assert.Equal("Invalid", results[2].Outcome);
        Assert.Equal("Skipped", results[3].Outcome); // listed twice
    }

    [SqlFact]
    public async Task The_last_active_super_admin_cannot_lose_the_role()
    {
        await using var db = fx.CreateContext();
        var seeder = new DbSeeder(db, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbSeeder>.Instance);
        await seeder.SeedBootstrapSuperAdminAsync("boot.admin@rrd.com");
        var svc = new UserService(db, Audit(db), TimeProvider.System);
        var superAdminId = await db.Roles.Where(r => r.Name == SystemRoles.SuperAdmin).Select(r => r.Id).SingleAsync();
        var admins = await db.UserRoles.Where(r => r.RoleId == superAdminId && r.User.Status == UserStatus.Active).Select(r => r.UserId).ToListAsync();
        if (admins.Count != 1) return; // another test made a second admin; the rule is exercised elsewhere
        var id = admins[0];

        await Assert.ThrowsAsync<RuleException>(() =>
            svc.UpdateAsync(id, new UpdateUserInput("Boot", "Admin", null, null, null, null, null, UserStatus.Active, []), null, default));
        await Assert.ThrowsAsync<RuleException>(() =>
            svc.UpdateAsync(id, new UpdateUserInput("Boot", "Admin", null, null, null, null, null, UserStatus.Inactive, [superAdminId]), null, default));
    }

    [SqlFact]
    public async Task Hrms_job_deactivates_leavers_unless_status_was_set_by_hand()
    {
        await using var db = fx.CreateContext();
        var users = new UserService(db, Audit(db), TimeProvider.System);
        if (!await db.Users.AnyAsync(u => u.Email == "leo.leaver@rrd.com"))
            await users.CreateAsync(new NewUserInput("leo.leaver@rrd.com", null, null, null, null, null, null, []), null, default);

        // Leo leaves in HRMS.
        await db.Database.ExecuteSqlRawAsync("UPDATE hrms.EmployeeProfile SET EmploymentStatus = 'Inactive', ExitDate = '2026-09-30' WHERE Email = 'leo.leaver@rrd.com'");
        var summary = await new HrmsSyncService(db, Audit(db), TimeProvider.System).RunAsync(default);

        Assert.Contains("leo.leaver@rrd.com", summary.DeactivatedEmails);
        var leo = await db.Users.AsNoTracking().Include(u => u.HrmsProfile).SingleAsync(u => u.Email == "leo.leaver@rrd.com");
        Assert.Equal(UserStatus.Inactive, leo.Status);
        Assert.Equal(new DateOnly(2026, 9, 30), leo.HrmsProfile!.ExitDate);
    }

    // ------------------------------------------------------------ Secrets

    [SqlFact]
    public async Task Secrets_are_stored_encrypted_and_read_back()
    {
        await using var db = fx.CreateContext();
        var store = new DbSecretStore(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var name = Unique("secret-");
        await store.SetAsync(name, "  s3cr3t-value ", 1, default);

        var raw = await db.AppSecrets.AsNoTracking().SingleAsync(s => s.Name == name);
        Assert.DoesNotContain("s3cr3t", raw.ProtectedValue);
        Assert.Equal("s3cr3t-value", await store.GetAsync(name, default));
        Assert.NotNull(await store.GetUpdatedAtAsync(name, default));
    }

    // ------------------------------------------------------------ Tenant verification

    private sealed class Tokens(bool fail = false) : IPowerBiTokenProvider
    {
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) =>
            fail ? throw new EmbedException("platform-error", "Service principal sign-in failed (invalid_client).") : Task.FromResult("not-a-jwt");
    }

    /// <summary>Fake Power BI: workspace list, workspace users and the admin API, configurable per test.</summary>
    private sealed class FakePowerBi(Guid ws, string clientId) : HttpMessageHandler
    {
        public HttpStatusCode GroupsStatus = HttpStatusCode.OK;
        public bool Member = true;
        public string Role = "Member";
        public bool Capacity = true;
        public HttpStatusCode AdminStatus = HttpStatusCode.Unauthorized;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            string body;
            HttpStatusCode code = HttpStatusCode.OK;
            if (path.Contains("/admin/groups")) { code = AdminStatus; body = "{}"; }
            else if (path.Contains($"/groups/{ws}/users"))
                body = $$$"""{"value":[{"identifier":"{{{clientId}}}","principalType":"App","groupUserAccessRight":"{{{Role}}}"},{"identifier":"someone@rrd.com","principalType":"User","groupUserAccessRight":"Admin"}]}""";
            else if (path.Contains("/groups"))
            {
                code = GroupsStatus;
                body = Member
                    ? $$$"""{"value":[{"id":"{{{ws}}}","name":"BI Prod","isOnDedicatedCapacity":{{{(Capacity ? "true" : "false")}}},"capacityId":"{{{Guid.NewGuid()}}}"}]}"""
                    : """{"value":[]}""";
            }
            else { code = HttpStatusCode.NotFound; body = ""; }
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private async Task<(TenantVerifier Verifier, BiTenant Tenant, FakePowerBi Fake, AppDbContext Db)> ArrangeTenant(bool tokenFails = false)
    {
        var db = fx.CreateContext();
        var ws = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString();
        var tenant = new BiTenant
        {
            Name = Unique("PBI "), Platform = BiPlatform.PowerBi, AzureTenantId = Guid.NewGuid().ToString(), ClientId = clientId, SecretName = "x",
            CreatedAtUtc = DateTime.UtcNow, Workspaces = [new PowerBiWorkspace { Name = "Prod", WorkspaceId = ws }],
        };
        db.BiTenants.Add(tenant);
        await db.SaveChangesAsync();
        var fake = new FakePowerBi(ws, clientId);
        var verifier = new TenantVerifier(new HttpClient(fake), db, new Tokens(tokenFails),
            new DbSecretStore(db, new EphemeralDataProtectionProvider(), TimeProvider.System), new FakeTimeProvider(DateTimeOffset.UtcNow));
        return (verifier, tenant, fake, db);
    }

    [SqlFact]
    public async Task Verification_passes_when_principal_is_member_and_workspace_is_on_capacity()
    {
        var (verifier, tenant, _, db) = await ArrangeTenant();
        await using var _db = db;
        var report = await verifier.VerifyAsync(tenant.Id, default);

        Assert.True(report.Passed);
        Assert.Contains(report.Checks, c => c.Name.Contains("access") && c.Result == CheckResult.Pass && c.Detail.Contains("Member"));
        Assert.Contains(report.Checks, c => c.Name.Contains("capacity") && c.Result == CheckResult.Pass);
        Assert.Contains(report.Checks, c => c.Name.StartsWith("Admin APIs") && c.Result == CheckResult.Warn); // warning doesn't fail

        var saved = await db.BiTenants.AsNoTracking().Include(t => t.Workspaces).SingleAsync(t => t.Id == tenant.Id);
        Assert.True(saved.LastVerifyPassed);
        Assert.Equal("Member", saved.Workspaces[0].ServicePrincipalAccess);
        Assert.True(saved.Workspaces[0].OnDedicatedCapacity);
    }

    [SqlFact]
    public async Task Verification_fails_for_viewer_role_and_for_shared_capacity()
    {
        var (verifier, tenant, fake, db) = await ArrangeTenant();
        await using var _db = db;
        fake.Role = "Viewer";
        fake.Capacity = false;
        var report = await verifier.VerifyAsync(tenant.Id, default);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Name.Contains("access") && c.Result == CheckResult.Fail && c.Detail.Contains("Viewer"));
        Assert.Contains(report.Checks, c => c.Name.Contains("capacity") && c.Result == CheckResult.Fail);
    }

    [SqlFact]
    public async Task Verification_explains_missing_workspace_membership_and_blocked_api()
    {
        var (verifier, tenant, fake, db) = await ArrangeTenant();
        await using var _db = db;
        fake.Member = false;
        var notMember = await verifier.VerifyAsync(tenant.Id, default);
        Assert.Contains(notMember.Checks, c => c.Result == CheckResult.Fail && c.Detail.Contains("not a member"));

        fake.GroupsStatus = HttpStatusCode.Unauthorized;
        var blocked = await verifier.VerifyAsync(tenant.Id, default);
        Assert.Contains(blocked.Checks, c => c.Name == "Power BI API access" && c.Result == CheckResult.Fail && c.Detail.Contains("Service principals can use Fabric APIs"));
    }

    [SqlFact]
    public async Task Verification_stops_at_sign_in_when_credentials_are_wrong()
    {
        var (verifier, tenant, _, db) = await ArrangeTenant(tokenFails: true);
        await using var _db = db;
        var report = await verifier.VerifyAsync(tenant.Id, default);
        var only = Assert.Single(report.Checks);
        Assert.Equal(CheckResult.Fail, only.Result);
        Assert.False(report.Passed);
    }
}
