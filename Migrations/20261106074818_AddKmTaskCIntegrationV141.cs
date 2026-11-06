using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmTaskCIntegrationV141 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                table: "document_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "supersedes_file_id",
                table: "document_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "document_files",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE document_files
                SET document_id = id,
                    version = 1;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "document_id",
                table: "document_files",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "version",
                table: "document_files",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "km_metadata_changes",
                columns: table => new
                {
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source_revision = table.Column<int>(type: "integer", nullable: false),
                    classification_json = table.Column<string>(type: "jsonb", nullable: false),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false),
                    changed_fields_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    km_change_job_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_error_retryable = table.Column<bool>(type: "boolean", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    authorized_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    authorized_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_metadata_changes", x => x.change_id);
                    table.CheckConstraint("ck_km_metadata_change_payload_sha", "length(payload_sha256) = 64");
                    table.CheckConstraint("ck_km_metadata_change_sent_job", "status NOT IN ('Sent','Processing','Completed') OR km_change_job_id IS NOT NULL");
                    table.CheckConstraint("ck_km_metadata_change_source", "source_system = 'CSIT' AND source_type IN ('TestReport','DocumentLibraryFile')");
                    table.CheckConstraint("ck_km_metadata_change_status", "status IN ('Pending','Dispatching','Sent','Processing','Completed','Failed')");
                    table.CheckConstraint("ck_km_metadata_change_version", "version >= 1 AND source_revision >= 2");
                    table.ForeignKey(
                        name: "FK_km_metadata_changes_km_analysis_review_revisions_review_rev~",
                        column: x => x.review_revision_id,
                        principalTable: "km_analysis_review_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_km_metadata_changes_km_document_analysis_results_analysis_r~",
                        column: x => x.analysis_result_id,
                        principalTable: "km_document_analysis_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "km_metadata_change_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    applied_revision = table.Column<int>(type: "integer", nullable: false),
                    payload_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_metadata_change_callbacks", x => x.callback_id);
                    table.CheckConstraint("ck_km_metadata_callback_revision", "applied_revision >= 0");
                    table.CheckConstraint("ck_km_metadata_callback_sha", "length(payload_sha256) = 64");
                    table.CheckConstraint("ck_km_metadata_callback_status", "status IN ('Processing','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_km_metadata_change_callbacks_km_metadata_changes_change_id",
                        column: x => x.change_id,
                        principalTable: "km_metadata_changes",
                        principalColumn: "change_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_document_files_document_id_version",
                table: "document_files",
                columns: new[] { "document_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_files_supersedes_file_id",
                table: "document_files",
                column: "supersedes_file_id",
                unique: true,
                filter: "supersedes_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_change_callbacks_change_id",
                table: "km_metadata_change_callbacks",
                column: "change_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_analysis_result_id",
                table: "km_metadata_changes",
                column: "analysis_result_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_km_change_job_id",
                table: "km_metadata_changes",
                column: "km_change_job_id",
                unique: true,
                filter: "km_change_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_review_revision_id",
                table: "km_metadata_changes",
                column: "review_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_source_type_source_entity_id_document_i~",
                table: "km_metadata_changes",
                columns: new[] { "source_type", "source_entity_id", "document_id", "version", "source_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_status_lease_expires_at",
                table: "km_metadata_changes",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_km_metadata_changes_status_next_retry_at",
                table: "km_metadata_changes",
                columns: new[] { "status", "next_retry_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_files_document_files_supersedes_file_id",
                table: "document_files",
                column: "supersedes_file_id",
                principalTable: "document_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_files_document_files_supersedes_file_id",
                table: "document_files");

            migrationBuilder.DropTable(
                name: "km_metadata_change_callbacks");

            migrationBuilder.DropTable(
                name: "km_metadata_changes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.DropIndex(
                name: "IX_document_files_document_id_version",
                table: "document_files");

            migrationBuilder.DropIndex(
                name: "IX_document_files_supersedes_file_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "document_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "supersedes_file_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "version",
                table: "document_files");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'DocumentLibraryFileUploaded' AND source_entity_type = 'DocumentLibraryFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type = 'VendorFileArchived' AND source_entity_type = 'VendorFile' AND source_entity_id = document_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL) OR (event_type IN ('EquipmentDocumentArchived', 'EquipmentAttachmentArchived') AND source_entity_type = 'ApparatusFile' AND source_entity_id = document_id AND event_id = source_entity_id AND version = 1 AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
        }
    }
}
