using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMySubmissionWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pending_submissions_status",
                table: "pending_submissions");

            migrationBuilder.AddColumn<Guid>(
                name: "resubmitted_as_submission_id",
                table: "pending_submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "resubmitted_at",
                table: "pending_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submitter_dismissed_at",
                table: "pending_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_pending_submissions_status",
                table: "pending_submissions",
                sql: "status IN ('Pending', 'ApprovedPendingCommit', 'Committed', 'Rejected', 'CommitFailed', 'Withdrawn')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pending_submissions_status",
                table: "pending_submissions");

            migrationBuilder.DropColumn(
                name: "resubmitted_as_submission_id",
                table: "pending_submissions");

            migrationBuilder.DropColumn(
                name: "resubmitted_at",
                table: "pending_submissions");

            migrationBuilder.DropColumn(
                name: "submitter_dismissed_at",
                table: "pending_submissions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pending_submissions_status",
                table: "pending_submissions",
                sql: "status IN ('Pending', 'ApprovedPendingCommit', 'Committed', 'Rejected', 'CommitFailed')");
        }
    }
}
