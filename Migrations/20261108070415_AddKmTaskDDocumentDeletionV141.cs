using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmTaskDDocumentDeletionV141 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_file_deletion_identity",
                table: "equipment_file_deletion_jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "canonical_ingestion_event_id",
                table: "equipment_file_deletion_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                table: "equipment_file_deletion_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE equipment_file_deletion_jobs AS deletion
                SET source_entity_type = 'EquipmentAttachment',
                    reason_code = 'SourceDeleted',
                    canonical_ingestion_event_id = ingestion.event_id
                FROM km_integration_outbox AS ingestion
                WHERE ingestion.source_entity_id = deletion.apparatus_file_id
                  AND ingestion.source_entity_type = 'ApparatusFile'
                  AND ingestion.event_type IN ('EquipmentDocumentArchived','EquipmentAttachmentArchived');

                UPDATE equipment_file_deletion_jobs
                SET source_entity_type = 'EquipmentAttachment',
                    reason_code = 'SourceDeleted'
                WHERE reason_code IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "reason_code",
                table: "equipment_file_deletion_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "km_v2_document_deletion_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deletion_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_v2_document_deletion_callbacks", x => x.callback_id);
                });

            migrationBuilder.CreateTable(
                name: "vendor_file_deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    canonical_ingestion_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    km_deletion_job_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_error_retryable = table.Column<bool>(type: "boolean", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    km_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    db_record_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    physical_file_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_file_deletion_jobs", x => x.id);
                    table.CheckConstraint("ck_vendor_file_deletion_identity", "source_type = 'VendorFile' AND vendor_file_id = document_id AND version = 1 AND reason_code IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup')");
                    table.CheckConstraint("ck_vendor_file_deletion_status", "status IN ('Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                });

            migrationBuilder.Sql("""
                INSERT INTO km_v2_document_deletion_callbacks
                    (callback_id, deletion_request_id, source_type, status, payload_sha256, received_at)
                SELECT callback_id, deletion_job_id, 'EquipmentAttachment', status,
                       payload_sha256, received_at
                FROM equipment_file_deletion_callbacks
                ON CONFLICT (callback_id) DO NOTHING;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_file_deletion_identity",
                table: "equipment_file_deletion_jobs",
                sql: "source_entity_type = 'EquipmentAttachment' AND apparatus_file_id = document_id AND version = 1 AND reason_code IN ('SourceDeleted','VersionSuperseded','AdministrativeCleanup')");

            migrationBuilder.CreateIndex(
                name: "IX_km_v2_document_deletion_callbacks_source_type_deletion_requ~",
                table: "km_v2_document_deletion_callbacks",
                columns: new[] { "source_type", "deletion_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_file_deletion_jobs_km_deletion_job_id",
                table: "vendor_file_deletion_jobs",
                column: "km_deletion_job_id",
                unique: true,
                filter: "km_deletion_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_file_deletion_jobs_status_updated_at",
                table: "vendor_file_deletion_jobs",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_file_deletion_jobs_vendor_file_id",
                table: "vendor_file_deletion_jobs",
                column: "vendor_file_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "km_v2_document_deletion_callbacks");

            migrationBuilder.DropTable(
                name: "vendor_file_deletion_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_file_deletion_identity",
                table: "equipment_file_deletion_jobs");

            migrationBuilder.DropColumn(
                name: "canonical_ingestion_event_id",
                table: "equipment_file_deletion_jobs");

            migrationBuilder.DropColumn(
                name: "reason_code",
                table: "equipment_file_deletion_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_file_deletion_identity",
                table: "equipment_file_deletion_jobs",
                sql: "source_entity_type = 'ApparatusFile' AND apparatus_file_id = document_id AND version = 1");
        }
    }
}
