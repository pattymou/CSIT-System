using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddKmDocumentAnalysisResultsV14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "km_analysis_callback_receipts",
                columns: table => new
                {
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_analysis_callback_receipts", x => x.callback_id);
                    table.CheckConstraint("ck_km_analysis_callback_sha256", "length(payload_sha256) = 64");
                    table.CheckConstraint("ck_km_analysis_callback_status", "status IN ('Processing','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_km_analysis_callback_receipts_km_integration_outbox_outbox_~",
                        column: x => x.outbox_id,
                        principalTable: "km_integration_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "km_document_analysis_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    callback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    csit_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    km_job_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    source_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    metadata_schema_version = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    analyzed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    indexed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    review_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    supersedes_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    superseded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    source_revision = table.Column<int>(type: "integer", nullable: false),
                    callback_payload_sha256 = table.Column<string>(type: "char(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_document_analysis_results", x => x.id);
                    table.CheckConstraint("ck_km_analysis_payload_sha256", "length(callback_payload_sha256) = 64");
                    table.CheckConstraint("ck_km_analysis_review_status", "review_status IN ('PendingVerification','Verified','Rejected')");
                    table.CheckConstraint("ck_km_analysis_schema_version", "metadata_schema_version IN ('1.0','2.0')");
                    table.CheckConstraint("ck_km_analysis_source_revision", "source_revision >= 1");
                    table.CheckConstraint("ck_km_analysis_source_sha256", "length(source_sha256) = 64");
                    table.CheckConstraint("ck_km_analysis_source_type", "source_type IN ('TestReport','DocumentLibraryFile','VerificationApplicationAttachment','VendorFile','EquipmentAttachment')");
                    table.CheckConstraint("ck_km_analysis_status", "status = 'Completed'");
                    table.CheckConstraint("ck_km_analysis_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_km_document_analysis_results_km_analysis_callback_receipts_~",
                        column: x => x.callback_id,
                        principalTable: "km_analysis_callback_receipts",
                        principalColumn: "callback_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_km_document_analysis_results_km_document_analysis_results_s~",
                        column: x => x.supersedes_result_id,
                        principalTable: "km_document_analysis_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_km_document_analysis_results_km_integration_outbox_outbox_id",
                        column: x => x.outbox_id,
                        principalTable: "km_integration_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "km_analysis_review_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    corrections_json = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reviewed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_km_analysis_review_revisions", x => x.id);
                    table.CheckConstraint("ck_km_analysis_review_decision", "decision IN ('Verified','Rejected')");
                    table.CheckConstraint("ck_km_analysis_review_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "FK_km_analysis_review_revisions_km_document_analysis_results_a~",
                        column: x => x.analysis_result_id,
                        principalTable: "km_document_analysis_results",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_km_analysis_callback_receipts_outbox_id",
                table: "km_analysis_callback_receipts",
                column: "outbox_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_analysis_review_revisions_analysis_result_id_revision",
                table: "km_analysis_review_revisions",
                columns: new[] { "analysis_result_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_analysis_result_id",
                table: "km_document_analysis_results",
                column: "analysis_result_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_callback_id",
                table: "km_document_analysis_results",
                column: "callback_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_document_id_version",
                table: "km_document_analysis_results",
                columns: new[] { "document_id", "version" });

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_outbox_id",
                table: "km_document_analysis_results",
                column: "outbox_id");

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_source_type_source_entity_id",
                table: "km_document_analysis_results",
                columns: new[] { "source_type", "source_entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_source_type_source_entity_id_d~",
                table: "km_document_analysis_results",
                columns: new[] { "source_type", "source_entity_id", "document_id", "version", "source_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_km_document_analysis_results_supersedes_result_id",
                table: "km_document_analysis_results",
                column: "supersedes_result_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "km_analysis_review_revisions");

            migrationBuilder.DropTable(
                name: "km_document_analysis_results");

            migrationBuilder.DropTable(
                name: "km_analysis_callback_receipts");
        }
    }
}
