using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260903090000_AddEnvironmentGroupOwnership")]
public partial class AddEnvironmentGroupOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "owner_team_option_id",
            table: "equipment_groups",
            type: "uuid",
            nullable: false);

        migrationBuilder.AddColumn<string>(
            name: "site",
            table: "equipment_groups",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false);

        migrationBuilder.CreateIndex(
            name: "IX_equipment_groups_owner_team_option_id",
            table: "equipment_groups",
            column: "owner_team_option_id");

        migrationBuilder.AddForeignKey(
            name: "FK_equipment_groups_system_options_owner_team_option_id",
            table: "equipment_groups",
            column: "owner_team_option_id",
            principalTable: "system_options",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_equipment_groups_system_options_owner_team_option_id",
            table: "equipment_groups");

        migrationBuilder.DropIndex(
            name: "IX_equipment_groups_owner_team_option_id",
            table: "equipment_groups");

        migrationBuilder.DropColumn(
            name: "owner_team_option_id",
            table: "equipment_groups");

        migrationBuilder.DropColumn(
            name: "site",
            table: "equipment_groups");
    }
}
