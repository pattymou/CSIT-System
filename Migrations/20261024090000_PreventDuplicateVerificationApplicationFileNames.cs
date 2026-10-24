using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261024090000_PreventDuplicateVerificationApplicationFileNames")]
public partial class PreventDuplicateVerificationApplicationFileNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX "IX_verification_application_files_application_lower_file_name"
            ON verification_application_files (verification_application_id, lower(file_name));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_verification_application_files_application_lower_file_name";
            """);
    }
}
