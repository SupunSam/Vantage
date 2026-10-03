using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "ApprovedCdns",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Host = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedCdns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSecrets",
                schema: "app",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProtectedValue = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSecrets", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DashboardId = table.Column<int>(type: "int", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BiServiceTypes",
                schema: "app",
                columns: table => new
                {
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RequiresFile = table.Column<bool>(type: "bit", nullable: false),
                    RequiresUrl = table.Column<bool>(type: "bit", nullable: false),
                    RequiresTenant = table.Column<bool>(type: "bit", nullable: false),
                    SupportsRls = table.Column<bool>(type: "bit", nullable: false),
                    SupportsRefresh = table.Column<bool>(type: "bit", nullable: false),
                    AllowedExtensions = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    MaxFileSizeMb = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiServiceTypes", x => x.Type);
                });

            migrationBuilder.CreateTable(
                name: "BiTenants",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    AzureTenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ServerUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SiteContentUrl = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConnectedAppClientId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConnectedAppSecretId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    VerifyUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TableauApiVersion = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastVerifyPassed = table.Column<bool>(type: "bit", nullable: true),
                    LastVerifyReport = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiTenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.CheckConstraint("CK_Categories_Level", "[Level] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "app",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DashboardViews",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ViewedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    ExternalEventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardViews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ToAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Template = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Link = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                schema: "app",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StatusSetManually = table.Column<bool>(type: "bit", nullable: false),
                    CognitoSub = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ContactNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TableauUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ServiceNowReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PowerBiWorkspaces",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ServicePrincipalAccess = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    OnDedicatedCapacity = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowerBiWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PowerBiWorkspaces_BiTenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "app",
                        principalTable: "BiTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                schema: "app",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    Module = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Module });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "app",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HrmsProfiles",
                schema: "app",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Department = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Division = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    JobGrade = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ManagerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmploymentStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExitDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrmsProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_HrmsProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalFolders",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalFolders_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "app",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    AssignedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "app",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dashboards",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    TenantId = table.Column<int>(type: "int", nullable: true),
                    WorkspaceId = table.Column<int>(type: "int", nullable: true),
                    PowerBiReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PowerBiDatasetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PowerBiImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TableauViewUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ThumbnailKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RlsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DataClassification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PrimaryOwnerId = table.Column<int>(type: "int", nullable: true),
                    BackupOwnerId = table.Column<int>(type: "int", nullable: true),
                    OwnershipPendingReview = table.Column<bool>(type: "bit", nullable: false),
                    SharePointFolder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SharePointSubFolder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastViewedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    InactivityFlaggedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                    table.CheckConstraint("CK_Dashboards_Owners", "[PrimaryOwnerId] IS NULL OR [BackupOwnerId] IS NULL OR [PrimaryOwnerId] <> [BackupOwnerId]");
                    table.ForeignKey(
                        name: "FK_Dashboards_BiTenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "app",
                        principalTable: "BiTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dashboards_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "app",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dashboards_PowerBiWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalSchema: "app",
                        principalTable: "PowerBiWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dashboards_Users_BackupOwnerId",
                        column: x => x.BackupOwnerId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dashboards_Users_PrimaryOwnerId",
                        column: x => x.PrimaryOwnerId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessReviews",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    OwnerUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RemindedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    EscalatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CompletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviews_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessReviews_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DashboardGroups",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RlsValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    ClonedFromGroupId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardGroups_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DashboardTags",
                schema: "app",
                columns: table => new
                {
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardTags", x => new { x.DashboardId, x.Tag });
                    table.ForeignKey(
                        name: "FK_DashboardTags_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardVersions",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    FileKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScanReport = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UploadedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardVersions_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalFolderItems",
                schema: "app",
                columns: table => new
                {
                    FolderId = table.Column<int>(type: "int", nullable: false),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalFolderItems", x => new { x.FolderId, x.DashboardId });
                    table.ForeignKey(
                        name: "FK_PersonalFolderItems_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonalFolderItems_PersonalFolders_FolderId",
                        column: x => x.FolderId,
                        principalSchema: "app",
                        principalTable: "PersonalFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Pins",
                schema: "app",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    PinnedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pins", x => new { x.UserId, x.DashboardId });
                    table.ForeignKey(
                        name: "FK_Pins_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pins_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshSchedules",
                schema: "app",
                columns: table => new
                {
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Days = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Times = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastPushedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastPushError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshSchedules", x => x.DashboardId);
                    table.ForeignKey(
                        name: "FK_RefreshSchedules_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRequests",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    RequesterUserId = table.Column<int>(type: "int", nullable: false),
                    RequesterComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AssignedGroupId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRequests_DashboardGroups_AssignedGroupId",
                        column: x => x.AssignedGroupId,
                        principalSchema: "app",
                        principalTable: "DashboardGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRequests_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    AddedByUserId = table.Column<int>(type: "int", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RemovedByUserId = table.Column<int>(type: "int", nullable: true),
                    RemovedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMembers_DashboardGroups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "app",
                        principalTable: "DashboardGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessReviewItems",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReviewId = table.Column<int>(type: "int", nullable: false),
                    GroupMemberId = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviewItems_AccessReviews_ReviewId",
                        column: x => x.ReviewId,
                        principalSchema: "app",
                        principalTable: "AccessReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessReviewItems_GroupMembers_GroupMemberId",
                        column: x => x.GroupMemberId,
                        principalSchema: "app",
                        principalTable: "GroupMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_AssignedGroupId",
                schema: "app",
                table: "AccessRequests",
                column: "AssignedGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_DashboardId_Status",
                schema: "app",
                table: "AccessRequests",
                columns: new[] { "DashboardId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_DecidedByUserId",
                schema: "app",
                table: "AccessRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_OnePending",
                schema: "app",
                table: "AccessRequests",
                columns: new[] { "DashboardId", "RequesterUserId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_RequesterUserId",
                schema: "app",
                table: "AccessRequests",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_GroupMemberId",
                schema: "app",
                table: "AccessReviewItems",
                column: "GroupMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_ReviewId_GroupMemberId",
                schema: "app",
                table: "AccessReviewItems",
                columns: new[] { "ReviewId", "GroupMemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviews_DashboardId_Period",
                schema: "app",
                table: "AccessReviews",
                columns: new[] { "DashboardId", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviews_OwnerUserId",
                schema: "app",
                table: "AccessReviews",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedCdns_Host",
                schema: "app",
                table: "ApprovedCdns",
                column: "Host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorUserId",
                schema: "app",
                table: "AuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_DashboardId",
                schema: "app",
                table: "AuditLogs",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                schema: "app",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredAtUtc",
                schema: "app",
                table: "AuditLogs",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BiTenants_Name",
                schema: "app",
                table: "BiTenants",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentId_Name",
                schema: "app",
                table: "Categories",
                columns: new[] { "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TopLevelName",
                schema: "app",
                table: "Categories",
                column: "Name",
                unique: true,
                filter: "[ParentId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardGroups_Name",
                schema: "app",
                table: "DashboardGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardGroups_OneDefault",
                schema: "app",
                table: "DashboardGroups",
                column: "DashboardId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_BackupOwnerId",
                schema: "app",
                table: "Dashboards",
                column: "BackupOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_CategoryId",
                schema: "app",
                table: "Dashboards",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_Code",
                schema: "app",
                table: "Dashboards",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_Name",
                schema: "app",
                table: "Dashboards",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_PowerBiReportId",
                schema: "app",
                table: "Dashboards",
                column: "PowerBiReportId",
                unique: true,
                filter: "[PowerBiReportId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_PrimaryOwnerId",
                schema: "app",
                table: "Dashboards",
                column: "PrimaryOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_Status",
                schema: "app",
                table: "Dashboards",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_TenantId",
                schema: "app",
                table: "Dashboards",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_WorkspaceId",
                schema: "app",
                table: "Dashboards",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardTags_Tag",
                schema: "app",
                table: "DashboardTags",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardVersions_DashboardId_VersionNumber",
                schema: "app",
                table: "DashboardVersions",
                columns: new[] { "DashboardId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardViews_DashboardId_ViewedAtUtc",
                schema: "app",
                table: "DashboardViews",
                columns: new[] { "DashboardId", "ViewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardViews_Source_ExternalEventId",
                schema: "app",
                table: "DashboardViews",
                columns: new[] { "Source", "ExternalEventId" },
                unique: true,
                filter: "[ExternalEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardViews_UserId_ViewedAtUtc",
                schema: "app",
                table: "DashboardViews",
                columns: new[] { "UserId", "ViewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Status_CreatedAtUtc",
                schema: "app",
                table: "EmailOutbox",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId_RemovedAtUtc",
                schema: "app",
                table: "GroupMembers",
                columns: new[] { "GroupId", "RemovedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_OneLiveGroupPerDashboard",
                schema: "app",
                table: "GroupMembers",
                columns: new[] { "DashboardId", "UserId" },
                unique: true,
                filter: "[RemovedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                schema: "app",
                table: "GroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HrmsProfiles_EmployeeId",
                schema: "app",
                table: "HrmsProfiles",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobName_StartedAtUtc",
                schema: "app",
                table: "JobRuns",
                columns: new[] { "JobName", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAtUtc",
                schema: "app",
                table: "Notifications",
                columns: new[] { "UserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalFolderItems_DashboardId",
                schema: "app",
                table: "PersonalFolderItems",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalFolders_UserId_Name",
                schema: "app",
                table: "PersonalFolders",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pins_DashboardId",
                schema: "app",
                table: "Pins",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_PowerBiWorkspaces_TenantId_WorkspaceId",
                schema: "app",
                table: "PowerBiWorkspaces",
                columns: new[] { "TenantId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                schema: "app",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                schema: "app",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_CognitoSub",
                schema: "app",
                table: "Users",
                column: "CognitoSub",
                unique: true,
                filter: "[CognitoSub] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "app",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRequests",
                schema: "app");

            migrationBuilder.DropTable(
                name: "AccessReviewItems",
                schema: "app");

            migrationBuilder.DropTable(
                name: "ApprovedCdns",
                schema: "app");

            migrationBuilder.DropTable(
                name: "AppSecrets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "app");

            migrationBuilder.DropTable(
                name: "BiServiceTypes",
                schema: "app");

            migrationBuilder.DropTable(
                name: "DashboardTags",
                schema: "app");

            migrationBuilder.DropTable(
                name: "DashboardVersions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "DashboardViews",
                schema: "app");

            migrationBuilder.DropTable(
                name: "EmailOutbox",
                schema: "app");

            migrationBuilder.DropTable(
                name: "HrmsProfiles",
                schema: "app");

            migrationBuilder.DropTable(
                name: "JobRuns",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "app");

            migrationBuilder.DropTable(
                name: "PersonalFolderItems",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Pins",
                schema: "app");

            migrationBuilder.DropTable(
                name: "RefreshSchedules",
                schema: "app");

            migrationBuilder.DropTable(
                name: "RolePermissions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "SystemSettings",
                schema: "app");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "app");

            migrationBuilder.DropTable(
                name: "AccessReviews",
                schema: "app");

            migrationBuilder.DropTable(
                name: "GroupMembers",
                schema: "app");

            migrationBuilder.DropTable(
                name: "PersonalFolders",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "app");

            migrationBuilder.DropTable(
                name: "DashboardGroups",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Dashboards",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "app");

            migrationBuilder.DropTable(
                name: "PowerBiWorkspaces",
                schema: "app");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "app");

            migrationBuilder.DropTable(
                name: "BiTenants",
                schema: "app");
        }
    }
}
