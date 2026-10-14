using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddGenericSupervisorApprovalPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "requires_supervisor_approval",
                table: "menu_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "approval_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_menu_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_module_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempt_number = table.Column<int>(type: "integer", nullable: true),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_audit_events", x => x.id);
                    table.CheckConstraint("ck_approval_audit_rejected_reason", "action <> 'Rejected' OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
                });

            migrationBuilder.CreateTable(
                name: "pending_submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    source_menu_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_module_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    submitted_by_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    submitted_team_option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_team_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reviewer_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reject_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    context_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    context_id = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    context_display = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    staging_storage_key = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    commit_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_commit_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_commit_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    commit_result_key = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    commit_lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    commit_lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_submissions", x => x.id);
                    table.CheckConstraint("ck_pending_submissions_committed_result", "status <> 'Committed' OR (commit_result_key IS NOT NULL AND length(btrim(commit_result_key)) > 0)");
                    table.CheckConstraint("ck_pending_submissions_file_metadata", "staging_storage_key IS NULL OR (original_file_name IS NOT NULL AND content_type IS NOT NULL AND file_size > 0 AND file_hash IS NOT NULL)");
                    table.CheckConstraint("ck_pending_submissions_rejected_reason", "status <> 'Rejected' OR (reviewer_account IS NOT NULL AND reviewed_at IS NOT NULL AND reject_reason IS NOT NULL AND length(btrim(reject_reason)) > 0)");
                    table.CheckConstraint("ck_pending_submissions_status", "status IN ('Pending', 'ApprovedPendingCommit', 'Committed', 'Rejected', 'CommitFailed')");
                    table.CheckConstraint("ck_pending_submissions_team", "submitted_team_option_id <> '00000000-0000-0000-0000-000000000000'::uuid AND length(btrim(submitted_team_code)) > 0");
                });

            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_approval_audit_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Approval audit events are append-only';
                END;
                $$;
                CREATE TRIGGER trg_approval_audit_append_only
                BEFORE UPDATE OR DELETE ON approval_audit_events
                FOR EACH ROW EXECUTE FUNCTION prevent_approval_audit_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_approval_audit_events_submission_id_occurred_at",
                table: "approval_audit_events",
                columns: new[] { "submission_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_source_menu_code_status",
                table: "pending_submissions",
                columns: new[] { "source_menu_code", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_status",
                table: "pending_submissions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_submission_key",
                table: "pending_submissions",
                column: "submission_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_submitted_at",
                table: "pending_submissions",
                column: "submitted_at");

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_submitted_by_account_status",
                table: "pending_submissions",
                columns: new[] { "submitted_by_account", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_submissions_submitted_team_option_id_status",
                table: "pending_submissions",
                columns: new[] { "submitted_team_option_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_audit_events");

            migrationBuilder.Sql("DROP FUNCTION prevent_approval_audit_mutation();");

            migrationBuilder.DropTable(
                name: "pending_submissions");

            migrationBuilder.DropColumn(
                name: "requires_supervisor_approval",
                table: "menu_items");
        }
    }
}
