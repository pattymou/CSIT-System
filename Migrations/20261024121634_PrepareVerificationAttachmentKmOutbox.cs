using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class PrepareVerificationAttachmentKmOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_test_report_event",
                table: "km_integration_outbox");

            migrationBuilder.AlterColumn<Guid>(
                name: "project_id",
                table: "km_integration_outbox",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND ((event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport' AND project_id IS NOT NULL) OR (event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile' AND project_id IS NULL AND case_id IS NULL AND task_id IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_km_outbox_supported_event",
                table: "km_integration_outbox");

            migrationBuilder.Sql(
                "DELETE FROM km_integration_outbox WHERE event_type = 'VerificationApplicationAttachmentArchived' AND source_entity_type = 'VerificationApplicationFile'");

            migrationBuilder.AlterColumn<Guid>(
                name: "project_id",
                table: "km_integration_outbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_km_outbox_test_report_event",
                table: "km_integration_outbox",
                sql: "source_system = 'CSIT' AND event_type = 'TestReportCommitted' AND source_entity_type = 'TestReport'");
        }
    }
}
