using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentVendorManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "archive_attempt_count",
                table: "apparatus_files",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "archive_error",
                table: "apparatus_files",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archive_next_retry_at",
                table: "apparatus_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "archive_status",
                table: "apparatus_files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archive_updated_at",
                table: "apparatus_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_json",
                table: "apparatus_files",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_source",
                table: "apparatus_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_verification_status",
                table: "apparatus_files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_version",
                table: "apparatus_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_file_name",
                table: "apparatus_files",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sha256",
                table: "apparatus_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "storage_folder_name",
                table: "apparatus",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "vendor_id",
                table: "apparatus",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "vendors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    aliases = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    types = table.Column<string[]>(type: "text[]", nullable: false),
                    website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    contact_person = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    address = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    storage_folder_name = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendors", x => x.id);
                    table.CheckConstraint("ck_vendors_types", "types <@ ARRAY['Manufacturer','Agent','Supplier','MaintenanceProvider']::text[]");
                });

            migrationBuilder.CreateTable(
                name: "vendor_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    folder_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    archive_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    archive_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    archive_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    archive_next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    archive_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    metadata_source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    metadata_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    metadata_verification_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_files", x => x.id);
                    table.CheckConstraint("ck_vendor_files_archive_result", "archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64)");
                    table.CheckConstraint("ck_vendor_files_archive_status", "archive_status IN ('Pending','Archived','Failed')");
                    table.CheckConstraint("ck_vendor_files_metadata_verification", "metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected')");
                    table.ForeignKey(
                        name: "FK_vendor_files_vendors_vendor_id",
                        column: x => x.vendor_id,
                        principalTable: "vendors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_files_archive_status_archive_next_retry_at",
                table: "apparatus_files",
                columns: new[] { "archive_status", "archive_next_retry_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_apparatus_files_archive_result",
                table: "apparatus_files",
                sql: "archive_status <> 'Archived' OR (sha256 IS NOT NULL AND length(sha256) = 64)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_apparatus_files_archive_status",
                table: "apparatus_files",
                sql: "archive_status IS NULL OR archive_status IN ('Pending','Archived','Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_apparatus_files_metadata_verification",
                table: "apparatus_files",
                sql: "metadata_verification_status IS NULL OR metadata_verification_status IN ('NotApplicable','PendingVerification','Verified','Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_vendor_id",
                table: "apparatus",
                column: "vendor_id");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_files_archive_status_archive_next_retry_at",
                table: "vendor_files",
                columns: new[] { "archive_status", "archive_next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_files_vendor_id_uploaded_at",
                table: "vendor_files",
                columns: new[] { "vendor_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_vendors_is_active_name",
                table: "vendors",
                columns: new[] { "is_active", "name" });

            migrationBuilder.CreateIndex(
                name: "IX_vendors_normalized_name",
                table: "vendors",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vendors_storage_folder_name",
                table: "vendors",
                column: "storage_folder_name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_apparatus_vendors_vendor_id",
                table: "apparatus",
                column: "vendor_id",
                principalTable: "vendors",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                INSERT INTO menu_items
                    (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
                     requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
                SELECT 'ea5b3405-74c3-497d-8336-391daaa3704f'::uuid, s.id, 'vendors',
                       '廠商資訊管理', '/vendors', '▦',
                       COALESCE((SELECT MAX(i.sort_order) + 10 FROM menu_items i WHERE i.section_id = s.id), 10),
                       TRUE, FALSE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
                FROM menu_sections s
                WHERE LOWER(s.code) = 'department'
                  AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'vendors');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM menu_items WHERE id = 'ea5b3405-74c3-497d-8336-391daaa3704f'::uuid;");

            migrationBuilder.DropForeignKey(
                name: "FK_apparatus_vendors_vendor_id",
                table: "apparatus");

            migrationBuilder.DropTable(
                name: "vendor_files");

            migrationBuilder.DropTable(
                name: "vendors");

            migrationBuilder.DropIndex(
                name: "IX_apparatus_files_archive_status_archive_next_retry_at",
                table: "apparatus_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_apparatus_files_archive_result",
                table: "apparatus_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_apparatus_files_archive_status",
                table: "apparatus_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_apparatus_files_metadata_verification",
                table: "apparatus_files");

            migrationBuilder.DropIndex(
                name: "IX_apparatus_vendor_id",
                table: "apparatus");

            migrationBuilder.DropColumn(
                name: "archive_attempt_count",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "archive_error",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "archive_next_retry_at",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "archive_status",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "archive_updated_at",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "metadata_json",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "metadata_source",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "metadata_verification_status",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "metadata_version",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "original_file_name",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "sha256",
                table: "apparatus_files");

            migrationBuilder.DropColumn(
                name: "storage_folder_name",
                table: "apparatus");

            migrationBuilder.DropColumn(
                name: "vendor_id",
                table: "apparatus");
        }
    }
}
