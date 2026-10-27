using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLibraryKmIntegrationV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_processing_jobs_status",
                table: "document_processing_jobs");

            migrationBuilder.AddColumn<int>(
                name: "archive_attempt_count",
                table: "document_processing_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "archive_last_attempt_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "archive_last_error",
                table: "document_processing_jobs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archive_lease_expires_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "archive_lease_token",
                table: "document_processing_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archive_next_retry_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "archive_sha256",
                table: "document_processing_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "archive_status",
                table: "document_processing_jobs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "km_completed_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "km_error_code",
                table: "document_processing_jobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "km_error_message",
                table: "document_processing_jobs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "km_error_retryable",
                table: "document_processing_jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "km_failed_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "km_started_at",
                table: "document_processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_json",
                table: "document_processing_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_processing_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_processing_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_processing_callbacks", x => x.callback_id);
                    table.CheckConstraint("ck_document_processing_callbacks_status", "status IN ('Processing', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "fk_document_callbacks_job",
                        column: x => x.document_processing_job_id,
                        principalTable: "document_processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.CreateIndex(
                name: "ix_document_jobs_archive_claim",
                table: "document_processing_jobs",
                columns: new[] { "archive_status", "archive_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_document_jobs_archive_retry",
                table: "document_processing_jobs",
                columns: new[] { "status", "archive_status", "archive_next_retry_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_processing_jobs_archive_result",
                table: "document_processing_jobs",
                sql: "archive_status <> 'Completed' OR (archived_at IS NOT NULL AND archive_sha256 IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_processing_jobs_archive_status",
                table: "document_processing_jobs",
                sql: "archive_status IN ('NotStarted', 'Pending', 'Archiving', 'Completed', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_processing_jobs_metadata",
                table: "document_processing_jobs",
                sql: "status <> 'Completed' OR metadata_json IS NOT NULL OR (archive_status = 'NotStarted' AND km_completed_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_processing_jobs_status",
                table: "document_processing_jobs",
                sql: "status IN ('Pending', 'Accepted', 'Processing', 'Completed', 'Failed')");

            migrationBuilder.CreateIndex(
                name: "ix_document_callbacks_job_received",
                table: "document_processing_callbacks",
                columns: new[] { "document_processing_job_id", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_processing_callbacks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropIndex(
                name: "ix_document_jobs_archive_claim",
                table: "document_processing_jobs");

            migrationBuilder.DropIndex(
                name: "ix_document_jobs_archive_retry",
                table: "document_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_processing_jobs_archive_result",
                table: "document_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_processing_jobs_archive_status",
                table: "document_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_processing_jobs_metadata",
                table: "document_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_processing_jobs_status",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_attempt_count",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_last_attempt_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_last_error",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_lease_expires_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_lease_token",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_next_retry_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_sha256",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archive_status",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_completed_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_error_code",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_error_message",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_error_retryable",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_failed_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "km_started_at",
                table: "document_processing_jobs");

            migrationBuilder.DropColumn(
                name: "metadata_json",
                table: "document_processing_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_processing_jobs_status",
                table: "document_processing_jobs",
                sql: "status IN ('Pending', 'Processing', 'Completed', 'Failed')");
        }
    }
}
