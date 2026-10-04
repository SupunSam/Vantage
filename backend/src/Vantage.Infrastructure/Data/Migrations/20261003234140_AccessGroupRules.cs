using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessGroupRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RequestedByUserId",
                schema: "app",
                table: "GroupAddRequests",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "Action",
                schema: "app",
                table: "GroupAddRequests",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Add"); // requests that existed before rules are all additions

            migrationBuilder.AddColumn<int>(
                name: "RuleId",
                schema: "app",
                table: "GroupAddRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleName",
                schema: "app",
                table: "GroupAddRequests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessGroupRules",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MoveFromOtherGroups = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastRunSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGroupRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessGroupRules_DashboardGroups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "app",
                        principalTable: "DashboardGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGroupRules_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessGroupRuleConditions",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGroupRuleConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessGroupRuleConditions_AccessGroupRules_RuleId",
                        column: x => x.RuleId,
                        principalSchema: "app",
                        principalTable: "AccessGroupRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequests_RuleId",
                schema: "app",
                table: "GroupAddRequests",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroupRuleConditions_RuleId",
                schema: "app",
                table: "AccessGroupRuleConditions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroupRules_CreatedByUserId",
                schema: "app",
                table: "AccessGroupRules",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGroupRules_GroupId",
                schema: "app",
                table: "AccessGroupRules",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupAddRequests_AccessGroupRules_RuleId",
                schema: "app",
                table: "GroupAddRequests",
                column: "RuleId",
                principalSchema: "app",
                principalTable: "AccessGroupRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupAddRequests_AccessGroupRules_RuleId",
                schema: "app",
                table: "GroupAddRequests");

            migrationBuilder.DropTable(
                name: "AccessGroupRuleConditions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "AccessGroupRules",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "IX_GroupAddRequests_RuleId",
                schema: "app",
                table: "GroupAddRequests");

            migrationBuilder.DropColumn(
                name: "Action",
                schema: "app",
                table: "GroupAddRequests");

            migrationBuilder.DropColumn(
                name: "RuleId",
                schema: "app",
                table: "GroupAddRequests");

            migrationBuilder.DropColumn(
                name: "RuleName",
                schema: "app",
                table: "GroupAddRequests");

            migrationBuilder.AlterColumn<int>(
                name: "RequestedByUserId",
                schema: "app",
                table: "GroupAddRequests",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
