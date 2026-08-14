using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260814140000_AddVerificationApplicationRouting")]
public partial class AddVerificationApplicationRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "verification_categories",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                module_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                leader_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                leader_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                display_order = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_verification_categories", x => x.id);
                table.CheckConstraint("ck_verification_categories_display_order", "display_order >= 0");
            });

        migrationBuilder.AlterColumn<string>(
            name: "module_code",
            table: "verification_applications",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100);

        migrationBuilder.AddColumn<Guid>(
            name: "verification_category_id",
            table: "verification_applications",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "category_code",
            table: "verification_applications",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "category_name",
            table: "verification_applications",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "assigned_leader_account",
            table: "verification_applications",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "assigned_leader_display_name",
            table: "verification_applications",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_verification_categories_code",
            table: "verification_categories",
            column: "code",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_verification_categories_is_active_display_order",
            table: "verification_categories",
            columns: new[] { "is_active", "display_order" });
        migrationBuilder.CreateIndex(
            name: "IX_verification_applications_applicant_account_status",
            table: "verification_applications",
            columns: new[] { "applicant_account", "status" });
        migrationBuilder.CreateIndex(
            name: "IX_verification_applications_assigned_leader_account_status",
            table: "verification_applications",
            columns: new[] { "assigned_leader_account", "status" });
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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_verification_applications_verification_categories_verificat~",
            table: "verification_applications");
        migrationBuilder.DropIndex(
            name: "IX_verification_applications_applicant_account_status",
            table: "verification_applications");
        migrationBuilder.DropIndex(
            name: "IX_verification_applications_assigned_leader_account_status",
            table: "verification_applications");
        migrationBuilder.DropIndex(
            name: "IX_verification_applications_verification_category_id_status",
            table: "verification_applications");
        migrationBuilder.DropColumn(name: "verification_category_id", table: "verification_applications");
        migrationBuilder.DropColumn(name: "category_code", table: "verification_applications");
        migrationBuilder.DropColumn(name: "category_name", table: "verification_applications");
        migrationBuilder.DropColumn(name: "assigned_leader_account", table: "verification_applications");
        migrationBuilder.DropColumn(name: "assigned_leader_display_name", table: "verification_applications");
        migrationBuilder.DropTable(name: "verification_categories");

        migrationBuilder.Sql("UPDATE verification_applications SET module_code = '' WHERE module_code IS NULL;");
        migrationBuilder.AlterColumn<string>(
            name: "module_code",
            table: "verification_applications",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100,
            oldNullable: true);
    }
}
