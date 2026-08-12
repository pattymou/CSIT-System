using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

/// <summary>
/// 對齊舊系統 ProjectTask.aspx 的附件欄位語意：
/// File_Name / File_Path / Upload_Date / Upload_Emp。
/// 新系統欄位對應：FileName / FilePath / CreatedAt / UploadEmp。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260424120000_AddUploadEmpToCaseFiles")]
public partial class AddUploadEmpToCaseFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE module_case_files
    ADD COLUMN IF NOT EXISTS upload_emp varchar(200);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE module_case_files
    DROP COLUMN IF EXISTS upload_emp;
""");
    }
}
