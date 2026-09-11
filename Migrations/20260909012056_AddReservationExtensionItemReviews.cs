using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

public partial class AddReservationExtensionItemReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "reservation_extension_item_reviews",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                reservation_extension_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                reservation_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                review_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                reviewed_by_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                reject_reason = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reservation_extension_item_reviews", x => x.id);
                table.CheckConstraint(
                    "ck_reservation_extension_item_reviews_status",
                    "review_status IN ('Pending', 'Approved', 'Rejected')");
                table.ForeignKey(
                    name: "FK_reservation_extension_item_reviews_reservation_extension_requests",
                    column: x => x.reservation_extension_request_id,
                    principalTable: "reservation_extension_requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_reservation_extension_item_reviews_reservation_items",
                    column: x => x.reservation_item_id,
                    principalTable: "reservation_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_extension_item_reviews_extension_item",
            table: "reservation_extension_item_reviews",
            columns: new[] { "reservation_extension_request_id", "reservation_item_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_extension_item_reviews_reservation_item_id",
            table: "reservation_extension_item_reviews",
            column: "reservation_item_id");

        migrationBuilder.CreateIndex(
            name: "IX_extension_item_reviews_status_extension",
            table: "reservation_extension_item_reviews",
            columns: new[] { "review_status", "reservation_extension_request_id" });

        migrationBuilder.Sql(
            """
            INSERT INTO reservation_extension_item_reviews
                (id, reservation_extension_request_id, reservation_item_id, review_status, created_at, updated_at)
            SELECT gen_random_uuid(), extension.id, item.id, 'Pending', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM reservation_extension_requests AS extension
            INNER JOIN reservations AS reservation ON reservation.id = extension.reservation_id
            INNER JOIN reservation_items AS item ON item.reservation_id = reservation.id
            WHERE extension.status = 'Pending'
              AND reservation.equipment_group_id IS NULL
              AND reservation.test_execution_profile_id IS NULL
            ON CONFLICT (reservation_extension_request_id, reservation_item_id) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "reservation_extension_item_reviews");
}
