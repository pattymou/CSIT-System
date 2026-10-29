using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentFileKmDeletionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_file_deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    apparatus_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    equipment_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("PK_equipment_file_deletion_jobs", x => x.id);
                    table.CheckConstraint("ck_equipment_file_deletion_identity", "source_entity_type = 'ApparatusFile' AND apparatus_file_id = document_id AND version = 1");
                    table.CheckConstraint("ck_equipment_file_deletion_status", "status IN ('Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                });

            migrationBuilder.CreateTable(
                name: "equipment_file_deletion_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deletion_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_file_deletion_callbacks", x => x.callback_id);
                    table.ForeignKey(
                        name: "FK_equipment_file_deletion_callbacks_equipment_file_deletion_j~",
                        column: x => x.deletion_job_id,
                        principalTable: "equipment_file_deletion_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_file_deletion_callbacks_deletion_job_id",
                table: "equipment_file_deletion_callbacks",
                column: "deletion_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_file_deletion_jobs_apparatus_file_id",
                table: "equipment_file_deletion_jobs",
                column: "apparatus_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equipment_file_deletion_jobs_km_deletion_job_id",
                table: "equipment_file_deletion_jobs",
                column: "km_deletion_job_id",
                unique: true,
                filter: "km_deletion_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_file_deletion_jobs_status_updated_at",
                table: "equipment_file_deletion_jobs",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'csit_runtime_dev') THEN
                        GRANT SELECT, INSERT, UPDATE ON TABLE equipment_file_deletion_jobs TO csit_runtime_dev;
                        REVOKE DELETE ON TABLE equipment_file_deletion_jobs FROM csit_runtime_dev;
                        GRANT SELECT, INSERT ON TABLE equipment_file_deletion_callbacks TO csit_runtime_dev;
                        REVOKE UPDATE, DELETE ON TABLE equipment_file_deletion_callbacks FROM csit_runtime_dev;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_file_deletion_callbacks");

            migrationBuilder.DropTable(
                name: "equipment_file_deletion_jobs");
        }
    }
}
