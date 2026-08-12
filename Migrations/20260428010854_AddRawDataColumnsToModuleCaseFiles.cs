using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRawDataColumnsToModuleCaseFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "nas_folder_path",
                table: "module_case_files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nas_file_path",
                table: "module_case_files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "raw_json_path",
                table: "module_case_files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_raw_data_exported",
                table: "module_case_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "raw_data_exported_at",
                table: "module_case_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "raw_data_export_error",
                table: "module_case_files",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "nas_folder_path", table: "module_case_files");
            migrationBuilder.DropColumn(name: "nas_file_path", table: "module_case_files");
            migrationBuilder.DropColumn(name: "raw_json_path", table: "module_case_files");
            migrationBuilder.DropColumn(name: "is_raw_data_exported", table: "module_case_files");
            migrationBuilder.DropColumn(name: "raw_data_exported_at", table: "module_case_files");
            migrationBuilder.DropColumn(name: "raw_data_export_error", table: "module_case_files");
        }
    }
}
