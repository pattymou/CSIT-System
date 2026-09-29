using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTestReportVersionReviewV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                table: "module_case_files",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "file_kind",
                table: "module_case_files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "finalized_at",
                table: "module_case_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finalized_by",
                table: "module_case_files",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_final",
                table: "module_case_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "review_comment",
                table: "module_case_files",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_status",
                table: "module_case_files",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "module_case_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewed_by",
                table: "module_case_files",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sha256",
                table: "module_case_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "module_case_files",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "version_no",
                table: "module_case_files",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // One-time compatibility backfill. The old implementation classified task files
            // outside the configured project root as TestReport.
            migrationBuilder.Sql(
                """
                UPDATE module_case_files AS f
                SET document_id = f.id,
                    version_no = 1,
                    updated_at = f.created_at,
                    file_kind = CASE
                        WHEN (f.task_id IS NOT NULL OR NULLIF(BTRIM(f.task_no), '') IS NOT NULL)
                         AND EXISTS (
                            SELECT 1
                            FROM module_records r
                            JOIN modules m ON m.id = r.module_id
                            JOIN system_options o
                              ON o.category = 'ThreeLevelRootPath'
                             AND LOWER(o.name) = LOWER(m.code)
                             AND o.is_enabled = TRUE
                            WHERE r.id = f.record_id
                              AND LOWER(REPLACE(f.file_path, E'\\', '/')) NOT LIKE
                                  LOWER(REPLACE(RTRIM(o.value, E'\\/'), E'\\', '/')) || '/%'
                         )
                        THEN 'TestReport'
                        ELSE 'Attachment'
                    END;

                UPDATE module_case_files
                SET review_status = 'Approved',
                    reviewed_at = created_at,
                    reviewed_by = COALESCE(NULLIF(BTRIM(upload_emp), ''), 'LegacyMigration'),
                    is_final = TRUE,
                    finalized_at = created_at,
                    finalized_by = COALESCE(NULLIF(BTRIM(upload_emp), ''), 'LegacyMigration')
                WHERE file_kind = 'TestReport';

                ALTER TABLE module_case_files ALTER COLUMN document_id DROP DEFAULT;
                ALTER TABLE module_case_files ALTER COLUMN file_kind DROP DEFAULT;
                ALTER TABLE module_case_files ALTER COLUMN updated_at DROP DEFAULT;
                ALTER TABLE module_case_files ALTER COLUMN version_no DROP DEFAULT;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_module_case_files_file_kind",
                table: "module_case_files",
                sql: "file_kind IN ('Attachment', 'TestReport')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_module_case_files_review_status",
                table: "module_case_files",
                sql: "review_status IS NULL OR review_status IN ('Pending', 'Approved', 'Rejected')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_module_case_files_version_no",
                table: "module_case_files",
                sql: "version_no > 0");

            migrationBuilder.CreateIndex(
                name: "IX_module_case_files_document_id",
                table: "module_case_files",
                column: "document_id",
                unique: true,
                filter: "\"is_final\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_module_case_files_document_id_version_no",
                table: "module_case_files",
                columns: new[] { "document_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_case_files_review_status_is_final",
                table: "module_case_files",
                columns: new[] { "review_status", "is_final" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_module_case_files_file_kind",
                table: "module_case_files");

            migrationBuilder.DropCheckConstraint(
                name: "CK_module_case_files_review_status",
                table: "module_case_files");

            migrationBuilder.DropCheckConstraint(
                name: "CK_module_case_files_version_no",
                table: "module_case_files");

            migrationBuilder.DropIndex(
                name: "IX_module_case_files_document_id",
                table: "module_case_files");

            migrationBuilder.DropIndex(
                name: "IX_module_case_files_document_id_version_no",
                table: "module_case_files");

            migrationBuilder.DropIndex(
                name: "IX_module_case_files_review_status_is_final",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "document_id",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "file_kind",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "finalized_at",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "finalized_by",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "is_final",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "review_comment",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "review_status",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "sha256",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "module_case_files");

            migrationBuilder.DropColumn(
                name: "version_no",
                table: "module_case_files");
        }
    }
}
