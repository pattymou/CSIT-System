using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "reservation_no_seq");

            migrationBuilder.CreateTable(
                name: "reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_no = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    applicant_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    applicant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    applicant_department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    applicant_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reject_reason = table.Column<string>(type: "text", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    borrowed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    borrowed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    returned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    returned_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_status", "status IN ('Draft', 'Pending', 'Approved', 'Borrowed', 'Returned', 'Rejected', 'Cancelled')");
                    table.CheckConstraint("ck_reservations_time_range", "start_time < end_time");
                });

            migrationBuilder.CreateTable(
                name: "reservation_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    apparatus_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    apparatus_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    products_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    brand = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    number = table.Column<string>(type: "text", nullable: true),
                    place = table.Column<string>(type: "text", nullable: true),
                    custodian = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    custodian_department = table.Column<string>(type: "text", nullable: true),
                    price_use = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_reservation_items_apparatus_apparatus_id",
                        column: x => x.apparatus_id,
                        principalTable: "apparatus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reservation_items_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_items_apparatus_id",
                table: "reservation_items",
                column: "apparatus_id");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_items_reservation_id",
                table: "reservation_items",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_items_reservation_id_apparatus_id",
                table: "reservation_items",
                columns: new[] { "reservation_id", "apparatus_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservations_applicant_account_status",
                table: "reservations",
                columns: new[] { "applicant_account", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_reservation_no",
                table: "reservations",
                column: "reservation_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservations_start_time_end_time",
                table: "reservations",
                columns: new[] { "start_time", "end_time" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_status",
                table: "reservations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_reservations_status_start_time_end_time",
                table: "reservations",
                columns: new[] { "status", "start_time", "end_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservation_items");

            migrationBuilder.DropTable(
                name: "reservations");

            migrationBuilder.DropSequence(
                name: "reservation_no_seq");
        }
    }
}
