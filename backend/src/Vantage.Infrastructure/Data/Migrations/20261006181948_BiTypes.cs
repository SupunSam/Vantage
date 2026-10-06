using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vantage.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BiTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // C56: "Dashboard Types" became "BI Types". Rename in place so the switches an admin already set are kept.
            migrationBuilder.DropPrimaryKey(name: "PK_BiServiceTypes", schema: "app", table: "BiServiceTypes");
            migrationBuilder.RenameTable(name: "BiServiceTypes", schema: "app", newName: "BiTypes", newSchema: "app");
            migrationBuilder.RenameColumn(name: "Type", schema: "app", table: "BiTypes", newName: "BiType");
            migrationBuilder.AddPrimaryKey(name: "PK_BiTypes", schema: "app", table: "BiTypes", column: "BiType");
            migrationBuilder.AddColumn<bool>(name: "HideWhenInactive", schema: "app", table: "BiTypes", type: "bit", nullable: false, defaultValue: false);
            migrationBuilder.RenameColumn(name: "Type", schema: "app", table: "Dashboards", newName: "BiType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "BiType", schema: "app", table: "Dashboards", newName: "Type");
            migrationBuilder.DropColumn(name: "HideWhenInactive", schema: "app", table: "BiTypes");
            migrationBuilder.DropPrimaryKey(name: "PK_BiTypes", schema: "app", table: "BiTypes");
            migrationBuilder.RenameColumn(name: "BiType", schema: "app", table: "BiTypes", newName: "Type");
            migrationBuilder.RenameTable(name: "BiTypes", schema: "app", newName: "BiServiceTypes", newSchema: "app");
            migrationBuilder.AddPrimaryKey(name: "PK_BiServiceTypes", schema: "app", table: "BiServiceTypes", column: "Type");
        }
    }
}
