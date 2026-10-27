using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationCaseLocalCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cleanup_manifest_json",
                table: "verification_case_deletion_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "verification_case_deletion_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "db_cleaned_at",
                table: "verification_case_deletion_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_cleanup_error_code",
                table: "verification_case_deletion_jobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cleanup_manifest_json",
                table: "verification_case_deletion_jobs");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "verification_case_deletion_jobs");

            migrationBuilder.DropColumn(
                name: "db_cleaned_at",
                table: "verification_case_deletion_jobs");

            migrationBuilder.DropColumn(
                name: "last_cleanup_error_code",
                table: "verification_case_deletion_jobs");
        }
    }
}
