using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupAddRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupAddRequests",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DashboardId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    ServiceNowReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MoveFromOtherGroups = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OverrideReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAddRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupAddRequests_DashboardGroups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "app",
                        principalTable: "DashboardGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAddRequests_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalSchema: "app",
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAddRequests_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAddRequests_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupAddRequestItems",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAddRequestItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupAddRequestItems_GroupAddRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "app",
                        principalTable: "GroupAddRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupAddRequestItems_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "app",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequestItems_RequestId_UserId",
                schema: "app",
                table: "GroupAddRequestItems",
                columns: new[] { "RequestId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequestItems_UserId_Decision",
                schema: "app",
                table: "GroupAddRequestItems",
                columns: new[] { "UserId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequests_DashboardId_Status",
                schema: "app",
                table: "GroupAddRequests",
                columns: new[] { "DashboardId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequests_DecidedByUserId",
                schema: "app",
                table: "GroupAddRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequests_GroupId_Status",
                schema: "app",
                table: "GroupAddRequests",
                columns: new[] { "GroupId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAddRequests_RequestedByUserId",
                schema: "app",
                table: "GroupAddRequests",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupAddRequestItems",
                schema: "app");

            migrationBuilder.DropTable(
                name: "GroupAddRequests",
                schema: "app");
        }
    }
}
