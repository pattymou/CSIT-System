using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentAggregateReanalysisAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.AddColumn<int>(
                name: "attempt_number",
                table: "equipment_aggregate_analysis_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                table: "equipment_aggregate_analysis_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_aggregate_request_attempt",
                table: "equipment_aggregate_analysis_requests",
                sql: "attempt_number >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~",
                table: "equipment_aggregate_analysis_requests",
                columns: new[] { "apparatus_id", "module_code", "manifest_hash", "analysis_version", "attempt_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM equipment_aggregate_analysis_requests WHERE attempt_number > 1) THEN
                        RAISE EXCEPTION 'Manual aggregate reanalysis history exists; reviewed forward repair required';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_aggregate_request_attempt",
                table: "equipment_aggregate_analysis_requests");
            migrationBuilder.DropIndex(
                name: "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "requested_by",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~",
                table: "equipment_aggregate_analysis_requests",
                columns: new[] { "apparatus_id", "module_code", "manifest_hash", "analysis_version" },
                unique: true);
        }
    }
}
