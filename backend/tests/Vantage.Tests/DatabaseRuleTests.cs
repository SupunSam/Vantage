using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>
/// Runs against a real SQL Server, because the key rules live in filtered unique indexes.
/// Set VANTAGE_TEST_SQL to a connection string with rights to create databases (e.g. the sa login of the local Docker SQL Server);
/// the tests create and drop their own database. Skipped when VANTAGE_TEST_SQL is not set.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public string? ConnectionString { get; } = BuildConnectionString();

    private static string? BuildConnectionString()
    {
        var baseCs = Environment.GetEnvironmentVariable("VANTAGE_TEST_SQL");
        if (string.IsNullOrWhiteSpace(baseCs)) return null;
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseCs) { InitialCatalog = "Vantage_Tests_" + Guid.NewGuid().ToString("N")[..8] };
        return b.ConnectionString;
    }

    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(ConnectionString!, sql => { sql.MigrationsHistoryTable("__EFMigrationsHistory", "app"); sql.EnableRetryOnFailure(3); }).Options);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        var seeder = new DbSeeder(db, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbSeeder>.Instance);
        await seeder.SeedReferenceDataAsync();
        // The HRMS mirror is created by the database init script in real setups; recreate it here with test rows.
        await db.Database.ExecuteSqlRawAsync(HrmsDdl);
    }

    private const string HrmsDdl = """
        IF SCHEMA_ID('hrms') IS NULL EXEC('CREATE SCHEMA hrms');
        IF OBJECT_ID('hrms.EmployeeProfile') IS NULL
        CREATE TABLE hrms.EmployeeProfile (
            EmployeeId NVARCHAR(20) NOT NULL PRIMARY KEY, Email NVARCHAR(256) NOT NULL UNIQUE,
            FirstName NVARCHAR(100) NOT NULL, LastName NVARCHAR(100) NOT NULL, DisplayName NVARCHAR(200) NULL,
            Department NVARCHAR(100) NULL, Division NVARCHAR(100) NULL, JobTitle NVARCHAR(150) NULL, JobGrade NVARCHAR(20) NULL,
            ManagerEmail NVARCHAR(256) NULL, Location NVARCHAR(100) NULL, EmploymentStatus NVARCHAR(20) NOT NULL,
            HireDate DATE NOT NULL, ExitDate DATE NULL, LastSyncedAtUtc DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME());
        INSERT INTO hrms.EmployeeProfile (EmployeeId, Email, FirstName, LastName, DisplayName, Department, Division, JobTitle, Location, EmploymentStatus, HireDate, ExitDate) VALUES
          ('T001', 'ada.active@rrd.com', 'Ada', 'Active', 'Ada Active', 'Finance', 'Corporate', 'Analyst', 'Colombo', 'Active', '2020-01-01', NULL),
          ('T002', 'leo.leaver@rrd.com', 'Leo', 'Leaver', 'Leo Leaver', 'Operations', 'Delivery', 'Lead', 'Chicago', 'Active', '2019-01-01', NULL),
          ('T003', 'gone.already@rrd.com', 'Gone', 'Already', 'Gone Already', 'Sales', 'Sales', 'Rep', 'Chennai', 'Inactive', '2018-01-01', '2026-08-31');
        """;

    public async Task DisposeAsync()
    {
        if (ConnectionString is null) return;
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VANTAGE_TEST_SQL")))
            Skip = "Set VANTAGE_TEST_SQL to run SQL Server rule tests.";
    }
}

public class DatabaseRuleTests(SqlServerFixture fx) : IClassFixture<SqlServerFixture>
{
    private sealed class TestContext : IRequestContext
    {
        public int? UserId => null;
        public string? CorrelationId => "test";
        public string? IpAddress => null;
    }

    private static async Task<User> NewUserAsync(AppDbContext db)
    {
        var u = new User { Email = $"{Guid.NewGuid():N}@rrd.com", UserType = UserType.Internal, CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static (DashboardFactory Factory, OwnershipService Ownership) Services(AppDbContext db)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ownership = new OwnershipService(db, clock);
        return (new DashboardFactory(db, ownership, new AuditWriter(db, new TestContext(), clock), clock), ownership);
    }

    private static Dashboard NewDashboard(int ownerId, int? backupId, bool rls = false) => new()
    {
        Code = "T" + Random.Shared.Next(100000, 999999), Name = "Test " + Guid.NewGuid().ToString("N")[..8],
        Type = BiType.PowerBi, Status = DashboardStatus.Active, PrimaryOwnerId = ownerId, BackupOwnerId = backupId, RlsEnabled = rls,
    };

    [SqlFact]
    public async Task Publishing_creates_default_group_with_both_owners_and_gives_owner_role()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var backup = await NewUserAsync(db);
        var (factory, _) = Services(db);

        var d = await factory.CreateAsync(NewDashboard(owner.Id, backup.Id, rls: true), "Finance_All", null);

        var group = await db.DashboardGroups.Include(g => g.Members).SingleAsync(g => g.DashboardId == d.Id);
        Assert.True(group.IsDefault);
        Assert.Equal($"{d.Code}-{d.Id}-default", group.Name);
        Assert.Equal("Finance_All", group.RlsValue);
        Assert.Equal(new[] { owner.Id, backup.Id }.Order(), group.Members.Select(m => m.UserId).Order());

        var ownerRoleId = await db.Roles.Where(r => r.Name == SystemRoles.DashboardOwner).Select(r => r.Id).SingleAsync();
        Assert.True(await db.UserRoles.AnyAsync(r => r.UserId == owner.Id && r.RoleId == ownerRoleId && r.IsAutomatic));
        Assert.True(await db.UserRoles.AnyAsync(r => r.UserId == backup.Id && r.RoleId == ownerRoleId));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "dashboard.created" && a.DashboardId == d.Id));
    }

    [SqlFact]
    public async Task Rls_dashboard_without_default_group_rls_value_is_refused()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var (factory, _) = Services(db);
        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(NewDashboard(owner.Id, null, rls: true), null, null));
    }

    [SqlFact]
    public async Task A_user_cannot_be_in_two_live_groups_of_the_same_dashboard()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var (factory, _) = Services(db);
        var d = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);

        var second = new DashboardGroup { DashboardId = d.Id, Name = $"{d.Code}-second", CreatedAtUtc = DateTime.UtcNow };
        db.DashboardGroups.Add(second);
        await db.SaveChangesAsync();

        db.GroupMembers.Add(new GroupMember { GroupId = second.Id, DashboardId = d.Id, UserId = owner.Id, AddedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task A_removed_membership_frees_the_user_to_join_another_group()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var (factory, _) = Services(db);
        var d = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);

        var live = await db.GroupMembers.SingleAsync(m => m.DashboardId == d.Id && m.UserId == owner.Id);
        live.RemovedAtUtc = DateTime.UtcNow;
        var second = new DashboardGroup { DashboardId = d.Id, Name = $"{d.Code}-other", CreatedAtUtc = DateTime.UtcNow };
        db.DashboardGroups.Add(second);
        await db.SaveChangesAsync();

        db.GroupMembers.Add(new GroupMember { GroupId = second.Id, DashboardId = d.Id, UserId = owner.Id, AddedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.GroupMembers.CountAsync(m => m.DashboardId == d.Id && m.UserId == owner.Id));
    }

    [SqlFact]
    public async Task Group_names_are_unique_across_the_portal()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var (factory, _) = Services(db);
        var a = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);
        var b = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);

        db.DashboardGroups.Add(new DashboardGroup { DashboardId = a.Id, Name = "SHARED-NAME", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.DashboardGroups.Add(new DashboardGroup { DashboardId = b.Id, Name = "SHARED-NAME", CreatedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task Owner_role_is_removed_automatically_when_the_user_owns_nothing()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var replacement = await NewUserAsync(db);
        var (factory, ownership) = Services(db);
        var d = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);

        d.PrimaryOwnerId = replacement.Id;
        await ownership.SyncOwnerRoleAsync(owner.Id);
        await ownership.SyncOwnerRoleAsync(replacement.Id);
        await db.SaveChangesAsync();

        var ownerRoleId = await db.Roles.Where(r => r.Name == SystemRoles.DashboardOwner).Select(r => r.Id).SingleAsync();
        Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == owner.Id && r.RoleId == ownerRoleId));
        Assert.True(await db.UserRoles.AnyAsync(r => r.UserId == replacement.Id && r.RoleId == ownerRoleId));
    }

    [SqlFact]
    public async Task Only_one_pending_request_per_user_per_dashboard()
    {
        await using var db = fx.CreateContext();
        var owner = await NewUserAsync(db);
        var requester = await NewUserAsync(db);
        var (factory, _) = Services(db);
        var d = await factory.CreateAsync(NewDashboard(owner.Id, null), null, null);

        db.AccessRequests.Add(new AccessRequest { DashboardId = d.Id, RequesterUserId = requester.Id, RequestedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.AccessRequests.Add(new AccessRequest { DashboardId = d.Id, RequesterUserId = requester.Id, RequestedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
