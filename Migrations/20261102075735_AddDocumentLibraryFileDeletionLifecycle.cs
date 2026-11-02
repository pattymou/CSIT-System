using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLibraryFileDeletionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_file_deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    storage_area = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    db_records_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    nas_file_removed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    staging_file_removed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approval_staging_removed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_file_deletion_jobs", x => x.id);
                    table.CheckConstraint("ck_document_file_deletion_snapshot", "file_size > 0 AND length(sha256) = 64 AND length(btrim(relative_path)) > 0");
                    table.CheckConstraint("ck_document_file_deletion_status", "status IN ('Preparing','Pending','Dispatching','Accepted','Processing','Failed','KmVerified','LocalCleanup','CleanupFailed','Completed')");
                });

            migrationBuilder.CreateTable(
                name: "document_file_deletion_callbacks",
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
                    table.PrimaryKey("PK_document_file_deletion_callbacks", x => x.callback_id);
                    table.ForeignKey(
                        name: "fk_document_file_deletion_callbacks_job",
                        column: x => x.deletion_job_id,
                        principalTable: "document_file_deletion_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_file_deletion_callbacks_deletion_job_id",
                table: "document_file_deletion_callbacks",
                column: "deletion_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_file_deletion_jobs_document_file_id",
                table: "document_file_deletion_jobs",
                column: "document_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_file_deletion_jobs_km_deletion_job_id",
                table: "document_file_deletion_jobs",
                column: "km_deletion_job_id",
                unique: true,
                filter: "km_deletion_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_document_file_deletion_jobs_status_updated_at",
                table: "document_file_deletion_jobs",
                columns: new[] { "status", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_file_deletion_callbacks");

            migrationBuilder.DropTable(
                name: "document_file_deletion_jobs");
        }
    }
}
