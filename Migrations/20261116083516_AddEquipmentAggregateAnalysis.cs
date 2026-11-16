using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentAggregateAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_aggregate_analysis_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    apparatus_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    module_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    analysis_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_documents_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_aggregate_analysis_requests", x => x.id);
                    table.CheckConstraint("ck_equipment_aggregate_request_manifest", "length(manifest_hash) = 64");
                    table.CheckConstraint("ck_equipment_aggregate_request_status", "status IN ('Pending','Completed','Failed','Superseded')");
                });

            migrationBuilder.CreateTable(
                name: "equipment_aggregate_analysis_callbacks",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_aggregate_analysis_callbacks", x => x.callback_id);
                    table.ForeignKey(
                        name: "FK_equipment_aggregate_analysis_callbacks_equipment_aggregate_~",
                        column: x => x.request_id,
                        principalTable: "equipment_aggregate_analysis_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "equipment_aggregate_analysis_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    apparatus_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    module_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    analysis_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_documents_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    analyzed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    review_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_aggregate_analysis_snapshots", x => x.id);
                    table.CheckConstraint("ck_equipment_aggregate_snapshot_manifest", "length(manifest_hash) = 64");
                    table.CheckConstraint("ck_equipment_aggregate_snapshot_review", "review_status IN ('PendingVerification','Verified','Rejected')");
                    table.ForeignKey(
                        name: "FK_equipment_aggregate_analysis_snapshots_equipment_aggregate_~",
                        column: x => x.request_id,
                        principalTable: "equipment_aggregate_analysis_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_callbacks_request_id",
                table: "equipment_aggregate_analysis_callbacks",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_apparatus_id_module_c~",
                table: "equipment_aggregate_analysis_requests",
                columns: new[] { "apparatus_id", "module_code", "manifest_hash", "analysis_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_requests_status_updated_at",
                table: "equipment_aggregate_analysis_requests",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_snapshots_apparatus_id_module_~",
                table: "equipment_aggregate_analysis_snapshots",
                columns: new[] { "apparatus_id", "module_code", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_aggregate_analysis_snapshots_request_id",
                table: "equipment_aggregate_analysis_snapshots",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_aggregate_analysis_callbacks");

            migrationBuilder.DropTable(
                name: "equipment_aggregate_analysis_snapshots");

            migrationBuilder.DropTable(
                name: "equipment_aggregate_analysis_requests");
        }
    }
}
