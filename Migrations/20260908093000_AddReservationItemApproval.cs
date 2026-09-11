using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260908093000_AddReservationItemApproval")]
public partial class AddReservationItemApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "review_status",
            table: "reservation_items",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reviewed_by_account",
            table: "reservation_items",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reviewed_by_name",
            table: "reservation_items",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "reviewed_at",
            table: "reservation_items",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reject_reason",
            table: "reservation_items",
            type: "text",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_reservation_items_review_status",
            table: "reservation_items",
            sql: "review_status IS NULL OR review_status IN ('Pending', 'Approved', 'Rejected')");

        migrationBuilder.Sql(
            """
            UPDATE reservation_items AS ri
            SET review_status = 'Pending'
            FROM reservations AS r
            WHERE r.id = ri.reservation_id
              AND r.status = 'Pending'
              AND r.equipment_group_id IS NULL
              AND r.test_execution_profile_id IS NULL
              AND ri.review_status IS NULL;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_reservation_items_review_status_reservation_id",
            table: "reservation_items",
            columns: new[] { "review_status", "reservation_id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_reservation_items_review_status_reservation_id",
            table: "reservation_items");

        migrationBuilder.DropCheckConstraint(
            name: "ck_reservation_items_review_status",
            table: "reservation_items");

        migrationBuilder.DropColumn(name: "review_status", table: "reservation_items");
        migrationBuilder.DropColumn(name: "reviewed_by_account", table: "reservation_items");
        migrationBuilder.DropColumn(name: "reviewed_by_name", table: "reservation_items");
        migrationBuilder.DropColumn(name: "reviewed_at", table: "reservation_items");
        migrationBuilder.DropColumn(name: "reject_reason", table: "reservation_items");
    }
}
