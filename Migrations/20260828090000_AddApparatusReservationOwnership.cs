using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828090000_AddApparatusReservationOwnership")]
public partial class AddApparatusReservationOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CustodianAccount",
            table: "apparatus",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "OwnerTeamOptionId",
            table: "apparatus",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_apparatus_CustodianAccount",
            table: "apparatus",
            column: "CustodianAccount");

        migrationBuilder.CreateIndex(
            name: "IX_apparatus_OwnerTeamOptionId",
            table: "apparatus",
            column: "OwnerTeamOptionId");

        migrationBuilder.AddForeignKey(
            name: "FK_apparatus_system_options_OwnerTeamOptionId",
            table: "apparatus",
            column: "OwnerTeamOptionId",
            principalTable: "system_options",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_apparatus_system_options_OwnerTeamOptionId",
            table: "apparatus");

        migrationBuilder.DropIndex(
            name: "IX_apparatus_CustodianAccount",
            table: "apparatus");

        migrationBuilder.DropIndex(
            name: "IX_apparatus_OwnerTeamOptionId",
            table: "apparatus");

        migrationBuilder.DropColumn(
            name: "CustodianAccount",
            table: "apparatus");

        migrationBuilder.DropColumn(
            name: "OwnerTeamOptionId",
            table: "apparatus");
    }
}
