using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

public partial class CompleteReservationExperiencePolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "applicant_extension", table: "reservations",
            type: "character varying(30)", maxLength: 30, nullable: true);

        migrationBuilder.CreateTable(
            name: "reservation_audit_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                actor_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                reason = table.Column<string>(type: "text", nullable: true),
                details = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reservation_audit_events", x => x.id);
                table.ForeignKey(
                    name: "FK_reservation_audit_events_reservations_reservation_id",
                    column: x => x.reservation_id, principalTable: "reservations", principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "reservation_extension_requests",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                current_end_time_snapshot = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                requested_end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                requested_by_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                reviewed_by_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                reject_reason = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reservation_extension_requests", x => x.id);
                table.CheckConstraint("ck_reservation_extension_requests_status", "status IN ('Pending', 'Approved', 'Rejected', 'Cancelled')");
                table.CheckConstraint("ck_reservation_extension_requests_time", "current_end_time_snapshot < requested_end_time");
                table.ForeignKey(
                    name: "FK_reservation_extension_requests_reservations_reservation_id",
                    column: x => x.reservation_id, principalTable: "reservations", principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_reservations_applicant_department_status_start_time_end_time",
            table: "reservations", columns: new[] { "applicant_department", "status", "start_time", "end_time" });
        migrationBuilder.CreateIndex(
            name: "IX_reservation_audit_events_reservation_id_occurred_at",
            table: "reservation_audit_events", columns: new[] { "reservation_id", "occurred_at" });
        migrationBuilder.CreateIndex(
            name: "IX_reservation_extension_requests_reservation_id",
            table: "reservation_extension_requests", column: "reservation_id", unique: true,
            filter: "status = 'Pending'");
        migrationBuilder.CreateIndex(
            name: "IX_reservation_extension_requests_status_requested_at",
            table: "reservation_extension_requests", columns: new[] { "status", "requested_at" });

        var seededAt = new DateTime(2026, 8, 24, 7, 10, 0, DateTimeKind.Utc);
        migrationBuilder.InsertData(
            table: "system_options",
            columns: new[] { "id", "category", "name", "value", "sort", "is_enabled", "note", "created_at", "updated_at" },
            values: new object[,]
            {
                { new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0101"), "Reservation", "reservation.max_borrow_days", "7", 10, true, "單次最長借用天數", seededAt, seededAt },
                { new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0102"), "Reservation", "reservation.department_max_concurrent_equipment", "0", 20, true, "部門同時借用設備上限；0 表示未限制", seededAt, seededAt },
                { new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0103"), "Reservation", "reservation.max_extension_days", "7", 30, true, "每次續借最長天數", seededAt, seededAt }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DeleteData(table: "system_options", keyColumn: "id", keyValues: new object[]
        {
            new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0101"),
            new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0102"),
            new Guid("8c3dcc77-7863-46a6-b750-4a65aa9d0103")
        });
        migrationBuilder.DropTable(name: "reservation_audit_events");
        migrationBuilder.DropTable(name: "reservation_extension_requests");
        migrationBuilder.DropIndex(name: "IX_reservations_applicant_department_status_start_time_end_time", table: "reservations");
        migrationBuilder.DropColumn(name: "applicant_extension", table: "reservations");
    }
}
