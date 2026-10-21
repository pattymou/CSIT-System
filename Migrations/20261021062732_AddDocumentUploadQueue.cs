using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentUploadQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "storage_area",
                table: "document_files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Library");

            migrationBuilder.CreateTable(
                name: "document_processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_processing_jobs", x => x.id);
                    table.CheckConstraint("ck_document_processing_jobs_status",
                        "status IN ('Pending', 'Processing', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_document_processing_jobs_document_files_document_file_id",
                        column: x => x.document_file_id,
                        principalTable: "document_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_processing_jobs_document_file_id",
                table: "document_processing_jobs",
                column: "document_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_processing_jobs_status_created_at",
                table: "document_processing_jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.Sql("""
                UPDATE menu_items AS item
                SET is_enabled = FALSE, updated_at = NOW()
                FROM menu_sections AS section
                WHERE item.section_id = section.id
                  AND lower(section.code) = 'department'
                  AND item.code IN ('verification', 'certification', 'equipment', 'goods');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE menu_items AS item
                SET is_enabled = TRUE, updated_at = NOW()
                FROM menu_sections AS section
                WHERE item.section_id = section.id
                  AND lower(section.code) = 'department'
                  AND item.code IN ('verification', 'certification', 'equipment', 'goods');
                """);

            migrationBuilder.DropTable(name: "document_processing_jobs");
            migrationBuilder.DropColumn(name: "storage_area", table: "document_files");
        }
    }
}
