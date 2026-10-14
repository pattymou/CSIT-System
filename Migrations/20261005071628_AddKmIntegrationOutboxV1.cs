using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmIntegrationOutboxV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "km_integration_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_system = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    km_job_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_integration_outbox", x => x.id);
                    table.CheckConstraint("ck_km_outbox_sent_job", "status NOT IN ('Sent', 'Processing', 'Completed') OR km_job_id IS NOT NULL");
                    table.CheckConstraint("ck_km_outbox_test_report_event", "source_system = 'CSIT' AND event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport'");
                    table.CheckConstraint("ck_km_outbox_version_size", "version > 0 AND size_bytes > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_event_id",
                table: "km_integration_outbox",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_km_job_id",
                table: "km_integration_outbox",
                column: "km_job_id",
                unique: true,
                filter: "km_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_source_entity_id",
                table: "km_integration_outbox",
                column: "source_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_source_system_document_id_version",
                table: "km_integration_outbox",
                columns: new[] { "source_system", "document_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_status_lease_expires_at",
                table: "km_integration_outbox",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_outbox_status_next_retry_at",
                table: "km_integration_outbox",
                columns: new[] { "status", "next_retry_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "km_integration_outbox");
        }
    }
}
