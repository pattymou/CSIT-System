using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationTwoLevelManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "actual_completion_date",
                table: "module_record_tasks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_test_item_code",
                table: "module_record_tasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_test_parameters_json",
                table: "module_record_tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_test_category_code",
                table: "module_record_cases",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_record_tasks_case_id_source_test_item_code",
                table: "module_record_tasks",
                columns: new[] { "case_id", "source_test_item_code" },
                unique: true,
                filter: "source_test_item_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_module_record_cases_record_id_source_test_category_code",
                table: "module_record_cases",
                columns: new[] { "record_id", "source_test_category_code" },
                unique: true,
                filter: "source_test_category_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_module_record_tasks_case_id_source_test_item_code",
                table: "module_record_tasks");

            migrationBuilder.DropIndex(
                name: "IX_module_record_cases_record_id_source_test_category_code",
                table: "module_record_cases");

            migrationBuilder.DropColumn(
                name: "actual_completion_date",
                table: "module_record_tasks");

            migrationBuilder.DropColumn(
                name: "source_test_item_code",
                table: "module_record_tasks");

            migrationBuilder.DropColumn(
                name: "source_test_parameters_json",
                table: "module_record_tasks");

            migrationBuilder.DropColumn(
                name: "source_test_category_code",
                table: "module_record_cases");

        }
    }
}
