using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260824043000_AddReservationEnvironmentContext")]
public partial class AddReservationEnvironmentContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("equipment_group_id", "reservations", "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("test_environment_id", "reservations", "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("test_execution_profile_id", "reservations", "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("equipment_group_code_snapshot", "reservations", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("equipment_group_name_snapshot", "reservations", "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>("test_environment_code_snapshot", "reservations", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("test_environment_name_snapshot", "reservations", "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>("test_execution_profile_code_snapshot", "reservations", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("test_execution_profile_name_snapshot", "reservations", "character varying(200)", maxLength: 200, nullable: true);

        migrationBuilder.AddColumn<Guid>("equipment_group_requirement_id", "reservation_items", "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("requirement_resource_type_snapshot", "reservation_items", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("requirement_capability_tag_snapshot", "reservation_items", "character varying(200)", maxLength: 200, nullable: true);

        migrationBuilder.CreateIndex("IX_reservations_equipment_group_id", "reservations", "equipment_group_id");
        migrationBuilder.CreateIndex("IX_reservations_test_environment_id", "reservations", "test_environment_id");
        migrationBuilder.CreateIndex("IX_reservations_test_execution_profile_id", "reservations", "test_execution_profile_id");
        migrationBuilder.CreateIndex("IX_reservation_items_equipment_group_requirement_id", "reservation_items", "equipment_group_requirement_id");

        migrationBuilder.AddForeignKey("FK_reservations_equipment_groups_equipment_group_id", "reservations", "equipment_group_id", "equipment_groups", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_reservations_test_environments_test_environment_id", "reservations", "test_environment_id", "test_environments", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_reservations_test_execution_profiles_test_execution_profile_id", "reservations", "test_execution_profile_id", "test_execution_profiles", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_reservation_items_equipment_group_requirements_equipment_group_requirement_id", "reservation_items", "equipment_group_requirement_id", "equipment_group_requirements", principalColumn: "id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_reservations_equipment_groups_equipment_group_id", "reservations");
        migrationBuilder.DropForeignKey("FK_reservations_test_environments_test_environment_id", "reservations");
        migrationBuilder.DropForeignKey("FK_reservations_test_execution_profiles_test_execution_profile_id", "reservations");
        migrationBuilder.DropForeignKey("FK_reservation_items_equipment_group_requirements_equipment_group_requirement_id", "reservation_items");
        migrationBuilder.DropIndex("IX_reservations_equipment_group_id", "reservations");
        migrationBuilder.DropIndex("IX_reservations_test_environment_id", "reservations");
        migrationBuilder.DropIndex("IX_reservations_test_execution_profile_id", "reservations");
        migrationBuilder.DropIndex("IX_reservation_items_equipment_group_requirement_id", "reservation_items");
        migrationBuilder.DropColumn("equipment_group_id", "reservations");
        migrationBuilder.DropColumn("test_environment_id", "reservations");
        migrationBuilder.DropColumn("test_execution_profile_id", "reservations");
        migrationBuilder.DropColumn("equipment_group_code_snapshot", "reservations");
        migrationBuilder.DropColumn("equipment_group_name_snapshot", "reservations");
        migrationBuilder.DropColumn("test_environment_code_snapshot", "reservations");
        migrationBuilder.DropColumn("test_environment_name_snapshot", "reservations");
        migrationBuilder.DropColumn("test_execution_profile_code_snapshot", "reservations");
        migrationBuilder.DropColumn("test_execution_profile_name_snapshot", "reservations");
        migrationBuilder.DropColumn("equipment_group_requirement_id", "reservation_items");
        migrationBuilder.DropColumn("requirement_resource_type_snapshot", "reservation_items");
        migrationBuilder.DropColumn("requirement_capability_tag_snapshot", "reservation_items");
    }
}
