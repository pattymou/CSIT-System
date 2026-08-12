using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260424150000_AddTaskLevelTestReportFiles")]
public partial class AddTaskLevelTestReportFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "task_id",
            table: "module_case_files",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "task_no",
            table: "module_case_files",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_module_case_files_task_id",
            table: "module_case_files",
            column: "task_id");

        migrationBuilder.AddForeignKey(
            name: "fk_module_case_files_module_record_tasks_task_id",
            table: "module_case_files",
            column: "task_id",
            principalTable: "module_record_tasks",
            principalColumn: "id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_module_case_files_module_record_tasks_task_id",
            table: "module_case_files");

        migrationBuilder.DropIndex(
            name: "ix_module_case_files_task_id",
            table: "module_case_files");

        migrationBuilder.DropColumn(
            name: "task_id",
            table: "module_case_files");

        migrationBuilder.DropColumn(
            name: "task_no",
            table: "module_case_files");
    }
}
