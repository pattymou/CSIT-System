using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentDocumentKmLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'EquipmentDocumentArchived' AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
        }
    }
}
