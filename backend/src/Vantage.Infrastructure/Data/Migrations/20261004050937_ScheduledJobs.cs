using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                schema: "app",
                table: "JobRuns",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Manual"); // runs before this migration were all started by the Users page button

            migrationBuilder.AddColumn<int>(
                name: "TriggeredByUserId",
                schema: "app",
                table: "JobRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobStates",
                schema: "app",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsPaused = table.Column<bool>(type: "bit", nullable: false),
                    Schedule = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RunningSinceUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStates", x => x.JobName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobStates",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "Trigger",
                schema: "app",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TriggeredByUserId",
                schema: "app",
                table: "JobRuns");
        }
    }
}
