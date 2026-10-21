using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    level = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_folders", x => x.id);
                    table.CheckConstraint("ck_document_folders_level", "level BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_document_folders_document_folders_parent_id",
                        column: x => x.parent_id,
                        principalTable: "document_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    standard_file_name = table.Column<string>(type: "character varying(350)", maxLength: 350, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    uploaded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_files_document_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "document_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_files_folder_id_uploaded_at",
                table: "document_files",
                columns: new[] { "folder_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_document_files_standard_file_name",
                table: "document_files",
                column: "standard_file_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_folders_parent_id_name",
                table: "document_folders",
                columns: new[] { "parent_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_folders_parent_id_sort_order",
                table: "document_folders",
                columns: new[] { "parent_id", "sort_order" });

            migrationBuilder.Sql("""
                INSERT INTO menu_items
                    (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
                     requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
                SELECT 'a2b4330d-62f0-4d1b-a725-ece359980c4c'::uuid, s.id, 'document_library',
                       '文件專區', '/documents', '▤',
                       COALESCE((SELECT MAX(i.sort_order) + 10 FROM menu_items i WHERE i.section_id = s.id), 10),
                       TRUE, FALSE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
                FROM menu_sections s
                WHERE LOWER(s.code) = 'department'
                  AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'document_library');

                INSERT INTO menu_items
                    (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
                     requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
                SELECT '67b9e4bc-2990-4e0c-a3d5-582cf95182da'::uuid, s.id, 'document_folder_management',
                       '文件資料夾管理', '/system/document-folders', '⚙',
                       COALESCE((SELECT MAX(i.sort_order) + 10 FROM menu_items i WHERE i.section_id = s.id), 10),
                       TRUE, TRUE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
                FROM menu_sections s
                WHERE LOWER(s.code) = 'administration'
                  AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'document_folder_management');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM menu_items WHERE code IN ('document_library', 'document_folder_management');");

            migrationBuilder.DropTable(
                name: "document_files");

            migrationBuilder.DropTable(
                name: "document_folders");
        }
    }
}
