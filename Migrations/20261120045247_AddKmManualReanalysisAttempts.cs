using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmManualReanalysisAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_km_integration_outbox_source_system_document_id_version",
                table: "km_integration_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_manual_retry_action",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.AddColumn<bool>(
                name: "last_error_retryable",
                table: "km_integration_outbox",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "manual_attempt_number",
                table: "km_integration_manual_retry_audits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "new_outbox_id",
                table: "km_integration_manual_retry_audits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_km_job_id",
                table: "km_integration_manual_retry_audits",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_source_system_document_id_version",
                table: "km_integration_outbox",
                columns: new[] { "source_system", "document_id", "version" },
                unique: true,
                filter: "status IN ('Pending', 'Dispatching', 'Sent', 'Processing', 'Completed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_manual_retry_audits_new_outbox_id",
                table: "km_integration_manual_retry_audits",
                column: "new_outbox_id",
                unique: true,
                filter: "new_outbox_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_manual_retry_action",
                table: "km_integration_manual_retry_audits",
                sql: "action IN ('ManualKmSyncRetry', 'ManualReanalysis') AND previous_status = 'Failed' AND (action <> 'ManualReanalysis' OR (new_outbox_id IS NOT NULL AND previous_km_job_id IS NOT NULL AND manual_attempt_number > 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM km_integration_manual_retry_audits
                             WHERE action = 'ManualReanalysis') OR
                     EXISTS (SELECT 1 FROM km_integration_outbox
                             GROUP BY source_system, document_id, version HAVING count(*) > 1)
                  THEN RAISE EXCEPTION 'Manual reanalysis history exists; restore from a reviewed backup instead of rolling back this migration.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_km_integration_outbox_source_system_document_id_version",
                table: "km_integration_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropIndex(
                name: "IX_km_integration_manual_retry_audits_new_outbox_id",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_manual_retry_action",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.DropColumn(
                name: "last_error_retryable",
                table: "km_integration_outbox");

            migrationBuilder.DropColumn(
                name: "manual_attempt_number",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.DropColumn(
                name: "new_outbox_id",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.DropColumn(
                name: "previous_km_job_id",
                table: "km_integration_manual_retry_audits");

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_source_system_document_id_version",
                table: "km_integration_outbox",
                columns: new[] { "source_system", "document_id", "version" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_manual_retry_action",
                table: "km_integration_manual_retry_audits",
                sql: "action = 'ManualKmSyncRetry' AND previous_status = 'Failed'");
        }
    }
}
