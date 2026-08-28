using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260817090000_RemoveModuleFromTeamRouting")]
public partial class RemoveModuleFromTeamRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_team_routings_modules_module_id",
            table: "team_routings");

        migrationBuilder.DropIndex(
            name: "IX_team_routings_is_enabled_sort",
            table: "team_routings");

        migrationBuilder.DropIndex(
            name: "IX_team_routings_module_id",
            table: "team_routings");

        migrationBuilder.DropCheckConstraint(
            name: "ck_team_routings_sort",
            table: "team_routings");

        migrationBuilder.DropColumn(
            name: "module_id",
            table: "team_routings");

        migrationBuilder.DropColumn(
            name: "sort",
            table: "team_routings");

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_is_enabled",
            table: "team_routings",
            column: "is_enabled");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_team_routings_is_enabled",
            table: "team_routings");

        migrationBuilder.AddColumn<Guid>(
            name: "module_id",
            table: "team_routings",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "sort",
            table: "team_routings",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql(
            "UPDATE team_routings " +
            "SET module_id = (SELECT id FROM modules WHERE code = 'verification' ORDER BY id LIMIT 1);");

        migrationBuilder.AlterColumn<Guid>(
            name: "module_id",
            table: "team_routings",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_team_routings_sort",
            table: "team_routings",
            sql: "sort >= 0");

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_is_enabled_sort",
            table: "team_routings",
            columns: new[] { "is_enabled", "sort" });

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_module_id",
            table: "team_routings",
            column: "module_id");

        migrationBuilder.AddForeignKey(
            name: "FK_team_routings_modules_module_id",
            table: "team_routings",
            column: "module_id",
            principalTable: "modules",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }
}
