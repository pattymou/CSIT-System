using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260904090000_AddEquipmentGroupDevices")]
public partial class AddEquipmentGroupDevices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "equipment_group_devices",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                equipment_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                apparatus_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                is_in_environment = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                added_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                presence_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                presence_updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                note = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_equipment_group_devices", x => x.id);
                table.ForeignKey(
                    name: "FK_equipment_group_devices_apparatus_apparatus_id",
                    column: x => x.apparatus_id,
                    principalTable: "apparatus",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_equipment_group_devices_equipment_groups_equipment_group_id",
                    column: x => x.equipment_group_id,
                    principalTable: "equipment_groups",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_equipment_group_devices_apparatus_id",
            table: "equipment_group_devices",
            column: "apparatus_id",
            unique: true,
            filter: "is_in_environment = true");

        migrationBuilder.CreateIndex(
            name: "IX_equipment_group_devices_equipment_group_id",
            table: "equipment_group_devices",
            column: "equipment_group_id");

        migrationBuilder.CreateIndex(
            name: "IX_equipment_group_devices_equipment_group_id_apparatus_id",
            table: "equipment_group_devices",
            columns: new[] { "equipment_group_id", "apparatus_id" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "equipment_group_devices");
    }
}
