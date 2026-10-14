using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmManualRetryAuditPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "km_integration_manual_retry_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    previous_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    previous_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    previous_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    previous_retry_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_integration_manual_retry_audits", x => x.id);
                    table.CheckConstraint("ck_km_manual_retry_reason", "length(btrim(reason)) BETWEEN 5 AND 500");
                    table.CheckConstraint("ck_km_manual_retry_action", "action = 'ManualKmSyncRetry' AND previous_status = 'Failed'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_km_integration_manual_retry_audits_outbox_id_requested_at",
                table: "km_integration_manual_retry_audits",
                columns: new[] { "outbox_id", "requested_at" });

            migrationBuilder.Sql("GRANT SELECT, INSERT ON TABLE km_integration_manual_retry_audits TO csit_runtime_dev;");
            // Existing DEV default privileges may grant all DML to the runtime role.
            // Keep this append-only audit table writable only through INSERT.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON TABLE km_integration_manual_retry_audits FROM csit_runtime_dev;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "km_integration_manual_retry_audits");
        }
    }
}
