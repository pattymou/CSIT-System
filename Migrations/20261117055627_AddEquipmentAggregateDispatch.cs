using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentAggregateDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "dispatch_attempt_count",
                table: "equipment_aggregate_analysis_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatch_last_attempt_at",
                table: "equipment_aggregate_analysis_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatch_last_error_code",
                table: "equipment_aggregate_analysis_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatch_last_error_message",
                table: "equipment_aggregate_analysis_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatch_lease_expires_at",
                table: "equipment_aggregate_analysis_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "dispatch_lease_token",
                table: "equipment_aggregate_analysis_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatch_next_retry_at",
                table: "equipment_aggregate_analysis_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatch_status",
                table: "equipment_aggregate_analysis_requests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "dispatched_at",
                table: "equipment_aggregate_analysis_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "km_job_id",
                table: "equipment_aggregate_analysis_requests",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_dispatch_lease_expire~",
                table: "equipment_aggregate_analysis_requests",
                column: "dispatch_lease_expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_km_job_id",
                table: "equipment_aggregate_analysis_requests",
                column: "km_job_id",
                unique: true,
                filter: "km_job_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_status_dispatch_statu~",
                table: "equipment_aggregate_analysis_requests",
                columns: new[] { "status", "dispatch_status", "dispatch_next_retry_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_job",
                table: "equipment_aggregate_analysis_requests",
                sql: "dispatch_status <> 'Accepted' OR km_job_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_lease",
                table: "equipment_aggregate_analysis_requests",
                sql: "(dispatch_status = 'Dispatching') = (dispatch_lease_token IS NOT NULL AND dispatch_lease_expires_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_status",
                table: "equipment_aggregate_analysis_requests",
                sql: "dispatch_status IN ('Pending','Dispatching','Accepted','RetryWaiting','Deferred','Failed','Superseded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_equipment_aggregate_analysis_requests_dispatch_lease_expire~",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropIndex(
                name: "IX_equipment_aggregate_analysis_requests_km_job_id",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropIndex(
                name: "IX_equipment_aggregate_analysis_requests_status_dispatch_statu~",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_job",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_lease",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_aggregate_dispatch_status",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_attempt_count",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_last_attempt_at",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_last_error_code",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_last_error_message",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_lease_expires_at",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_lease_token",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_next_retry_at",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatch_status",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "equipment_aggregate_analysis_requests");

            migrationBuilder.DropColumn(
                name: "km_job_id",
                table: "equipment_aggregate_analysis_requests");
        }
    }
}
