using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

/// <summary>
/// 對齊舊系統 TaskEdit.aspx / ProjectTask.aspx 的子任務欄位：
/// Sub PU、機種名稱、實驗室名稱、報價金額、請款金額。
/// 指派工程師仍沿用 assign_engineer，開始/預計完成/結果/狀態/進度/備註沿用既有欄位。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260424130000_AlignModuleTaskFieldsWithLegacy")]
public partial class AlignModuleTaskFieldsWithLegacy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE module_record_tasks
    ADD COLUMN IF NOT EXISTS sub_pu varchar(200),
    ADD COLUMN IF NOT EXISTS model_name varchar(200),
    ADD COLUMN IF NOT EXISTS lab varchar(200),
    ADD COLUMN IF NOT EXISTS quoted varchar(100),
    ADD COLUMN IF NOT EXISTS reimburse varchar(100);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE module_record_tasks
    DROP COLUMN IF EXISTS sub_pu,
    DROP COLUMN IF EXISTS model_name,
    DROP COLUMN IF EXISTS lab,
    DROP COLUMN IF EXISTS quoted,
    DROP COLUMN IF EXISTS reimburse;
""");
    }
}
