using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260817070000_RefactorVerificationRoutingToSystemTeamMaster")]
public partial class RefactorVerificationRoutingToSystemTeamMaster : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_verification_applications_verification_categories_verificat~",
            table: "verification_applications");

        migrationBuilder.DropIndex(
            name: "IX_verification_applications_verification_category_id_status",
            table: "verification_applications");

        migrationBuilder.AddColumn<Guid>(
            name: "team_option_id",
            table: "verification_applications",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "team_code",
            table: "verification_applications",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "team_name",
            table: "verification_applications",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "team_routings",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                team_option_id = table.Column<Guid>(type: "uuid", nullable: false),
                leader_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                leader_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                module_id = table.Column<Guid>(type: "uuid", nullable: false),
                is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                sort = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_team_routings", x => x.id);
                table.CheckConstraint("ck_team_routings_sort", "sort >= 0");
                table.ForeignKey(
                    name: "FK_team_routings_modules_module_id",
                    column: x => x.module_id,
                    principalTable: "modules",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_team_routings_system_options_team_option_id",
                    column: x => x.team_option_id,
                    principalTable: "system_options",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_verification_applications_team_option_id_status",
            table: "verification_applications",
            columns: new[] { "team_option_id", "status" });

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_is_enabled_sort",
            table: "team_routings",
            columns: new[] { "is_enabled", "sort" });

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_module_id",
            table: "team_routings",
            column: "module_id");

        migrationBuilder.CreateIndex(
            name: "IX_team_routings_team_option_id",
            table: "team_routings",
            column: "team_option_id",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_verification_applications_system_options_team_option_id",
            table: "verification_applications",
            column: "team_option_id",
            principalTable: "system_options",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_verification_applications_system_options_team_option_id",
            table: "verification_applications");

        migrationBuilder.DropTable(name: "team_routings");

        migrationBuilder.DropIndex(
            name: "IX_verification_applications_team_option_id_status",
            table: "verification_applications");

        migrationBuilder.DropColumn(name: "team_option_id", table: "verification_applications");
        migrationBuilder.DropColumn(name: "team_code", table: "verification_applications");
        migrationBuilder.DropColumn(name: "team_name", table: "verification_applications");

        migrationBuilder.CreateIndex(
            name: "IX_verification_applications_verification_category_id_status",
            table: "verification_applications",
            columns: new[] { "verification_category_id", "status" });

        migrationBuilder.AddForeignKey(
            name: "FK_verification_applications_verification_categories_verificat~",
            table: "verification_applications",
            column: "verification_category_id",
            principalTable: "verification_categories",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }
}
