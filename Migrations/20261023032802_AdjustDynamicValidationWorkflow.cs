using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AdjustDynamicValidationWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "expected_end_date",
                table: "verification_applications",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "team_option_id",
                table: "validation_product_types",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_product_types_team_option_id",
                table: "validation_product_types",
                column: "team_option_id");

            migrationBuilder.AddForeignKey(
                name: "FK_validation_product_types_system_options_team_option_id",
                table: "validation_product_types",
                column: "team_option_id",
                principalTable: "system_options",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                UPDATE validation_product_types
                SET team_option_id = (
                    SELECT id FROM system_options
                    WHERE category = 'Team' AND is_enabled
                      AND lower(value) IN ('wi-fi team', 'wifi team', 'wireless team')
                    ORDER BY CASE lower(value) WHEN 'wi-fi team' THEN 0 WHEN 'wifi team' THEN 1 ELSE 2 END
                    LIMIT 1)
                WHERE code IN ('WIFI_AP','WIFI_STATION','FIVE_G_FWA','FIVE_G_ODU','FIVE_G_DONGLE_WIFI','FIVE_G_DONGLE_NO_WIFI');

                UPDATE validation_product_types
                SET team_option_id = (
                    SELECT id FROM system_options
                    WHERE category = 'Team' AND is_enabled
                      AND lower(value) IN ('smallcell team', 'smallcell')
                    ORDER BY CASE lower(value) WHEN 'smallcell team' THEN 0 ELSE 1 END
                    LIMIT 1)
                WHERE code IN ('SMALLCELL','O_RU');

                UPDATE validation_test_item_definitions AS item
                SET is_mandatory = FALSE
                FROM validation_test_item_categories AS category,
                     validation_form_template_versions AS version
                WHERE item.test_item_category_id = category.id
                  AND category.template_version_id = version.id
                  AND version.status = 'Draft'
                  AND item.was_published = FALSE
                  AND item.is_mandatory = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE validation_test_item_definitions AS item
                SET is_mandatory = TRUE
                FROM validation_test_item_categories AS category,
                     validation_form_template_versions AS version
                WHERE item.test_item_category_id = category.id
                  AND category.template_version_id = version.id
                  AND version.status = 'Draft'
                  AND item.was_published = FALSE
                  AND item.code IN ('CELLULAR_OTA_THROUGHPUT','CELLULAR_TRP_TIS','WIFI_OTA_THROUGHPUT','WIFI_RVR','WIFI_TRP_TIS');
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_validation_product_types_system_options_team_option_id",
                table: "validation_product_types");

            migrationBuilder.DropIndex(
                name: "IX_validation_product_types_team_option_id",
                table: "validation_product_types");

            migrationBuilder.DropColumn(
                name: "expected_end_date",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "team_option_id",
                table: "validation_product_types");
        }
    }
}
