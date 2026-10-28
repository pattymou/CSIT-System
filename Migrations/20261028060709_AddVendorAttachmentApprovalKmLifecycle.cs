using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorAttachmentApprovalKmLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.AddColumn<Guid>(
                name: "approval_operation_id",
                table: "vendor_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "approved_at",
                table: "vendor_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_by",
                table: "vendor_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_vendor_files_approval_operation_id",
                table: "vendor_files",
                column: "approval_operation_id",
                unique: true,
                filter: "approval_operation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_vendor_files_approval",
                table: "vendor_files",
                sql: "(approval_operation_id IS NULL AND approved_by IS NULL AND approved_at IS NULL) OR (approval_operation_id IS NOT NULL AND approved_by IS NOT NULL AND length(btrim(approved_by)) > 0 AND approved_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM menu_items WHERE code = 'vendors') THEN
                        RAISE EXCEPTION 'Required vendors menu item is missing.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM menu_items WHERE code = 'vendors'
                               AND module_code IS NOT NULL AND module_code <> 'vendor') THEN
                        RAISE EXCEPTION 'vendors menu item has an unexpected module_code.';
                    END IF;
                    UPDATE menu_items
                       SET module_code = 'vendor', requires_supervisor_approval = TRUE,
                           updated_at = NOW()
                     WHERE code = 'vendors';
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE menu_items
                   SET module_code = NULL, requires_supervisor_approval = FALSE,
                       updated_at = NOW()
                 WHERE code = 'vendors' AND module_code = 'vendor';
                """);

            migrationBuilder.DropIndex(
                name: "IX_vendor_files_approval_operation_id",
                table: "vendor_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vendor_files_approval",
                table: "vendor_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropColumn(
                name: "approval_operation_id",
                table: "vendor_files");

            migrationBuilder.DropColumn(
                name: "approved_at",
                table: "vendor_files");

            migrationBuilder.DropColumn(
                name: "approved_by",
                table: "vendor_files");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
        }
    }
}
