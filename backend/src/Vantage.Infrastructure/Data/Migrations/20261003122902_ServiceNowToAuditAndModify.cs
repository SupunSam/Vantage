using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <summary>ServiceNow reference moves from Users to AuditLogs; Dashboards get the Modify Dashboard import fields.</summary>
    public partial class ServiceNowToAuditAndModify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplaceImportId",
                schema: "app",
                table: "Dashboards",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplaceVersionNumber",
                schema: "app",
                table: "Dashboards",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceNowReference",
                schema: "app",
                table: "AuditLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // ServiceNow references now belong to the audit log, not the user record: keep any that were entered.
            migrationBuilder.Sql(@"
INSERT INTO app.AuditLogs (OccurredAtUtc, ActorUserId, Action, EntityType, EntityId, Details, ServiceNowReference)
SELECT SYSUTCDATETIME(), NULL, 'user.servicenow-reference-moved', 'User', CAST(Id AS nvarchar(64)),
       N'{""note"":""Moved from the user record when ServiceNow references became part of the audit log""}', ServiceNowReference
FROM app.Users WHERE ServiceNowReference IS NOT NULL AND LTRIM(RTRIM(ServiceNowReference)) <> '';");

            migrationBuilder.DropColumn(
                name: "ServiceNowReference",
                schema: "app",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ServiceNowReference",
                schema: "app",
                table: "AuditLogs",
                column: "ServiceNowReference",
                filter: "[ServiceNowReference] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ServiceNowReference",
                schema: "app",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ReplaceImportId",
                schema: "app",
                table: "Dashboards");

            migrationBuilder.DropColumn(
                name: "ReplaceVersionNumber",
                schema: "app",
                table: "Dashboards");

            migrationBuilder.DropColumn(
                name: "ServiceNowReference",
                schema: "app",
                table: "AuditLogs");

            migrationBuilder.AddColumn<string>(
                name: "ServiceNowReference",
                schema: "app",
                table: "Users",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
