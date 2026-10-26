using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationCaseDeletionWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verification_case_deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_case_deletion_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "verification_case_deletion_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: true),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    km_deletion_job_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_retryable = table.Column<bool>(type: "boolean", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    km_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_case_deletion_targets", x => x.id);
                    table.ForeignKey(
                        name: "FK_verification_case_deletion_targets_verification_case_deleti~",
                        column: x => x.job_id,
                        principalTable: "verification_case_deletion_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "verification_case_deletion_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_case_deletion_callbacks", x => x.callback_id);
                    table.ForeignKey(
                        name: "FK_verification_case_deletion_callbacks_verification_case_dele~",
                        column: x => x.target_id,
                        principalTable: "verification_case_deletion_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_callbacks_target_id",
                table: "verification_case_deletion_callbacks",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_jobs_record_id",
                table: "verification_case_deletion_jobs",
                column: "record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_targets_job_id_document_file_id",
                table: "verification_case_deletion_targets",
                columns: new[] { "job_id", "document_file_id" },
                unique: true,
                filter: "document_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_targets_job_id_source_type_sourc~",
                table: "verification_case_deletion_targets",
                columns: new[] { "job_id", "source_type", "source_entity_id", "version" },
                unique: true,
                filter: "source_entity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_targets_km_deletion_job_id",
                table: "verification_case_deletion_targets",
                column: "km_deletion_job_id",
                unique: true,
                filter: "km_deletion_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_verification_case_deletion_targets_status_updated_at",
                table: "verification_case_deletion_targets",
                columns: new[] { "status", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_case_deletion_callbacks");

            migrationBuilder.DropTable(
                name: "verification_case_deletion_targets");

            migrationBuilder.DropTable(
                name: "verification_case_deletion_jobs");
        }
    }
}
