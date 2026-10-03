using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;

namespace Vantage.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<HrmsProfile> HrmsProfiles => Set<HrmsProfile>();
    public DbSet<HrmsEmployeeSource> HrmsSource => Set<HrmsEmployeeSource>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<BiTenant> BiTenants => Set<BiTenant>();
    public DbSet<PowerBiWorkspace> PowerBiWorkspaces => Set<PowerBiWorkspace>();
    public DbSet<BiServiceType> BiServiceTypes => Set<BiServiceType>();
    public DbSet<ApprovedCdn> ApprovedCdns => Set<ApprovedCdn>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AppSecret> AppSecrets => Set<AppSecret>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<DashboardTag> DashboardTags => Set<DashboardTag>();
    public DbSet<DashboardVersion> DashboardVersions => Set<DashboardVersion>();
    public DbSet<RefreshSchedule> RefreshSchedules => Set<RefreshSchedule>();
    public DbSet<DashboardGroup> DashboardGroups => Set<DashboardGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AccessReview> AccessReviews => Set<AccessReview>();
    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();
    public DbSet<Pin> Pins => Set<Pin>();
    public DbSet<PersonalFolder> PersonalFolders => Set<PersonalFolder>();
    public DbSet<PersonalFolderItem> PersonalFolderItems => Set<PersonalFolderItem>();
    public DbSet<DashboardView> DashboardViews => Set<DashboardView>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Enums are stored as readable strings.
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);
        b.Properties<string>().HaveMaxLength(256);
        b.Properties<DateTime>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema("app");

        // ---------- Identity ----------
        m.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.CognitoSub).IsUnique().HasFilter("[CognitoSub] IS NOT NULL");
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.ContactNumber).HasMaxLength(40);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.TableauUserName).HasMaxLength(256);
            e.HasOne(x => x.HrmsProfile).WithOne(x => x.User).HasForeignKey<HrmsProfile>(x => x.UserId);
        });

        m.Entity<HrmsProfile>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.EmployeeId).IsUnique();
            e.Property(x => x.EmployeeId).HasMaxLength(20);
            e.Property(x => x.JobGrade).HasMaxLength(20);
            e.Property(x => x.EmploymentStatus).HasMaxLength(20);
        });

        // Mirror table owned by the SQL-level HRMS sync; created by the database init script, not by migrations.
        m.Entity<HrmsEmployeeSource>(e =>
        {
            e.ToTable("EmployeeProfile", "hrms", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.EmployeeId);
            e.Property(x => x.LastSyncedAtUtc).HasPrecision(0);
        });

        m.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(500);
        });

        m.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.Module });
            e.Property(x => x.Module).HasMaxLength(40);
            e.HasOne(x => x.Role).WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId);
        });

        m.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(x => x.Roles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(x => x.Users).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- Catalog ----------
        m.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            // Names unique within their parent; top-level names unique among themselves.
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique().HasFilter("[ParentId] IS NOT NULL");
            e.HasIndex(x => x.Name).IsUnique().HasFilter("[ParentId] IS NULL").HasDatabaseName("IX_Categories_TopLevelName");
            e.ToTable(t => t.HasCheckConstraint("CK_Categories_Level", "[Level] BETWEEN 1 AND 3"));
        });

        m.Entity<BiTenant>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.AzureTenantId).HasMaxLength(64);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.ServerUrl).HasMaxLength(400);
            e.Property(x => x.SiteContentUrl).HasMaxLength(100);
            e.Property(x => x.ConnectedAppClientId).HasMaxLength(64);
            e.Property(x => x.ConnectedAppSecretId).HasMaxLength(64);
            e.Property(x => x.SecretName).HasMaxLength(128);
            e.Property(x => x.VerifyUserName).HasMaxLength(256);
            e.Property(x => x.TableauApiVersion).HasMaxLength(10);
            e.Property(x => x.LastVerifyReport).HasMaxLength(-1);
        });

        m.Entity<AppSecret>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.ProtectedValue).HasMaxLength(-1);
        });

        m.Entity<PowerBiWorkspace>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ServicePrincipalAccess).HasMaxLength(30);
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany(x => x.Workspaces).HasForeignKey(x => x.TenantId);
        });

        m.Entity<BiServiceType>(e =>
        {
            e.HasKey(x => x.Type);
            e.Property(x => x.DisplayName).HasMaxLength(60);
            e.Property(x => x.AllowedExtensions).HasMaxLength(60);
        });

        m.Entity<ApprovedCdn>(e => e.HasIndex(x => x.Host).IsUnique());

        m.Entity<SystemSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000);
            e.Property(x => x.Description).HasMaxLength(500);
        });

        // ---------- Dashboards ----------
        m.Entity<Dashboard>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Code);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PowerBiReportId).IsUnique().HasFilter("[PowerBiReportId] IS NOT NULL");
            e.Property(x => x.Code).HasMaxLength(Rules.DashboardCodeMax);
            e.Property(x => x.Name).HasMaxLength(Rules.DashboardNameMax);
            e.Property(x => x.Description).HasMaxLength(Rules.DescriptionMax);
            e.Property(x => x.TableauViewUrl).HasMaxLength(1000);
            e.Property(x => x.ThumbnailKey).HasMaxLength(400);
            e.Property(x => x.SharePointFolder).HasMaxLength(400);
            e.Property(x => x.SharePointSubFolder).HasMaxLength(400);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrimaryOwner).WithMany().HasForeignKey(x => x.PrimaryOwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BackupOwner).WithMany().HasForeignKey(x => x.BackupOwnerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Dashboards_Owners", "[PrimaryOwnerId] IS NULL OR [BackupOwnerId] IS NULL OR [PrimaryOwnerId] <> [BackupOwnerId]"));
        });

        m.Entity<DashboardTag>(e =>
        {
            e.HasKey(x => new { x.DashboardId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(Rules.TagMax);
            e.HasIndex(x => x.Tag);
            e.HasOne(x => x.Dashboard).WithMany(x => x.Tags).HasForeignKey(x => x.DashboardId);
        });

        m.Entity<DashboardVersion>(e =>
        {
            e.HasIndex(x => new { x.DashboardId, x.VersionNumber }).IsUnique();
            e.Property(x => x.FileKey).HasMaxLength(400);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ScanReport).HasMaxLength(4000);
            e.HasOne(x => x.Dashboard).WithMany(x => x.Versions).HasForeignKey(x => x.DashboardId);
        });

        m.Entity<RefreshSchedule>(e =>
        {
            e.HasKey(x => x.DashboardId);
            e.Property(x => x.Days).HasMaxLength(100);
            e.Property(x => x.Times).HasMaxLength(200);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.LastPushError).HasMaxLength(2000);
            e.HasOne(x => x.Dashboard).WithOne(x => x.RefreshSchedule).HasForeignKey<RefreshSchedule>(x => x.DashboardId);
        });

        m.Entity<DashboardGroup>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(Rules.GroupNameMax);
            e.Property(x => x.RlsValue).HasMaxLength(100);
            // One default group per dashboard.
            e.HasIndex(x => x.DashboardId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("IX_DashboardGroups_OneDefault");
            e.HasOne(x => x.Dashboard).WithMany(x => x.Groups).HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<GroupMember>(e =>
        {
            // A user is in at most one live group per dashboard.
            e.HasIndex(x => new { x.DashboardId, x.UserId }).IsUnique().HasFilter("[RemovedAtUtc] IS NULL")
                .HasDatabaseName("IX_GroupMembers_OneLiveGroupPerDashboard");
            e.HasIndex(x => new { x.GroupId, x.RemovedAtUtc });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.RemovedReason).HasMaxLength(500);
            e.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Dashboard>().WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- Access ----------
        m.Entity<AccessRequest>(e =>
        {
            e.HasIndex(x => new { x.DashboardId, x.Status });
            // One open request per user per dashboard.
            e.HasIndex(x => new { x.DashboardId, x.RequesterUserId }).IsUnique().HasFilter("[Status] = 'Pending'")
                .HasDatabaseName("IX_AccessRequests_OnePending");
            e.Property(x => x.RequesterComment).HasMaxLength(1000);
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.HasOne(x => x.Dashboard).WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedGroup).WithMany().HasForeignKey(x => x.AssignedGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AccessReview>(e =>
        {
            e.HasIndex(x => new { x.DashboardId, x.Period }).IsUnique();
            e.HasOne(x => x.Dashboard).WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AccessReviewItem>(e =>
        {
            e.HasIndex(x => new { x.ReviewId, x.GroupMemberId }).IsUnique();
            e.HasOne(x => x.Review).WithMany(x => x.Items).HasForeignKey(x => x.ReviewId);
            e.HasOne(x => x.GroupMember).WithMany().HasForeignKey(x => x.GroupMemberId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Pin>(e =>
        {
            e.HasKey(x => new { x.UserId, x.DashboardId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Dashboard).WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<PersonalFolder>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        m.Entity<PersonalFolderItem>(e =>
        {
            e.HasKey(x => new { x.FolderId, x.DashboardId });
            e.HasOne(x => x.Folder).WithMany(x => x.Items).HasForeignKey(x => x.FolderId);
            e.HasOne(x => x.Dashboard).WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Restrict);
        });

        // ---------- Operations ----------
        m.Entity<DashboardView>(e =>
        {
            e.HasIndex(x => new { x.DashboardId, x.ViewedAtUtc });
            e.HasIndex(x => new { x.UserId, x.ViewedAtUtc });
            e.HasIndex(x => new { x.Source, x.ExternalEventId }).IsUnique().HasFilter("[ExternalEventId] IS NOT NULL");
            e.Property(x => x.ExternalEventId).HasMaxLength(100);
        });

        m.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.DashboardId);
            e.HasIndex(x => x.ActorUserId);
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(-1);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.ServiceNowReference).HasMaxLength(64);
            e.HasIndex(x => x.ServiceNowReference).HasFilter("[ServiceNowReference] IS NOT NULL");
        });

        m.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReadAtUtc });
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Link).HasMaxLength(400);
        });

        m.Entity<EmailOutbox>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.HtmlBody).HasMaxLength(-1);
            e.Property(x => x.Template).HasMaxLength(60);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });

        m.Entity<JobRun>(e =>
        {
            e.HasIndex(x => new { x.JobName, x.StartedAtUtc });
            e.Property(x => x.JobName).HasMaxLength(60);
            e.Property(x => x.Summary).HasMaxLength(4000);
        });
    }
}
