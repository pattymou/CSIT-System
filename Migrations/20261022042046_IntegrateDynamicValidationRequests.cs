using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class IntegrateDynamicValidationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "validation_form_payload_json",
                table: "verification_applications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_product_type_code",
                table: "verification_applications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "validation_template_version_id",
                table: "verification_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "validation_template_version_number",
                table: "verification_applications",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE menu_items
                   SET title = '驗證申請專區', route_path = '/validation-request-applications', updated_at = NOW()
                 WHERE code = 'validation_requests';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE menu_items
                   SET title = '驗證測試申請單', route_path = '/validation-requests', updated_at = NOW()
                 WHERE code = 'validation_requests';
                """);

            migrationBuilder.DropColumn(
                name: "validation_form_payload_json",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "validation_product_type_code",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "validation_template_version_id",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "validation_template_version_number",
                table: "verification_applications");
        }
    }
}
