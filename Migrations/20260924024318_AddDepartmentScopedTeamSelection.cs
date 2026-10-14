using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentScopedTeamSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "routing_department_code",
                table: "verification_applications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_department_name",
                table: "verification_applications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "routing_department_option_id",
                table: "verification_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentOptionId",
                table: "apparatus",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_applications_routing_department_option_id",
                table: "verification_applications",
                column: "routing_department_option_id");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_DepartmentOptionId",
                table: "apparatus",
                column: "DepartmentOptionId");

            migrationBuilder.AddForeignKey(
                name: "FK_apparatus_system_options_DepartmentOptionId",
                table: "apparatus",
                column: "DepartmentOptionId",
                principalTable: "system_options",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_verification_applications_system_options_routing_department~",
                table: "verification_applications",
                column: "routing_department_option_id",
                principalTable: "system_options",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_apparatus_system_options_DepartmentOptionId",
                table: "apparatus");

            migrationBuilder.DropForeignKey(
                name: "FK_verification_applications_system_options_routing_department~",
                table: "verification_applications");

            migrationBuilder.DropIndex(
                name: "IX_verification_applications_routing_department_option_id",
                table: "verification_applications");

            migrationBuilder.DropIndex(
                name: "IX_apparatus_DepartmentOptionId",
                table: "apparatus");

            migrationBuilder.DropColumn(
                name: "routing_department_code",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "routing_department_name",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "routing_department_option_id",
                table: "verification_applications");

            migrationBuilder.DropColumn(
                name: "DepartmentOptionId",
                table: "apparatus");
        }
    }
}
