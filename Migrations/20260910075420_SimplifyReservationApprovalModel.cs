using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyReservationApprovalModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservation_extension_item_reviews");

            migrationBuilder.DropIndex(
                name: "IX_reservation_items_review_status_reservation_id",
                table: "reservation_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservation_items_review_status",
                table: "reservation_items");

            migrationBuilder.DropColumn(
                name: "reject_reason",
                table: "reservation_items");

            migrationBuilder.DropColumn(
                name: "review_status",
                table: "reservation_items");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "reservation_items");

            migrationBuilder.DropColumn(
                name: "reviewed_by_account",
                table: "reservation_items");

            migrationBuilder.DropColumn(
                name: "reviewed_by_name",
                table: "reservation_items");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reject_reason",
                table: "reservation_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_status",
                table: "reservation_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "reservation_items",
                type: "timestamp with time zone",
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

            migrationBuilder.CreateTable(
                name: "reservation_extension_item_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_extension_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reject_reason = table.Column<string>(type: "text", nullable: true),
                    review_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_extension_item_reviews", x => x.id);
                    table.CheckConstraint("ck_reservation_extension_item_reviews_status", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_reservation_extension_item_reviews_reservation_extension_re~",
                        column: x => x.reservation_extension_request_id,
                        principalTable: "reservation_extension_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reservation_extension_item_reviews_reservation_items_reserv~",
                        column: x => x.reservation_item_id,
                        principalTable: "reservation_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_items_review_status_reservation_id",
                table: "reservation_items",
                columns: new[] { "review_status", "reservation_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservation_items_review_status",
                table: "reservation_items",
                sql: "review_status IS NULL OR review_status IN ('Pending', 'Approved', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_extension_item_reviews_reservation_extension_re~",
                table: "reservation_extension_item_reviews",
                columns: new[] { "reservation_extension_request_id", "reservation_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservation_extension_item_reviews_reservation_item_id",
                table: "reservation_extension_item_reviews",
                column: "reservation_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_reservation_extension_item_reviews_review_status_reservatio~",
                table: "reservation_extension_item_reviews",
                columns: new[] { "review_status", "reservation_extension_request_id" });
        }
    }
}
