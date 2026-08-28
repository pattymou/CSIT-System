using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddApparatusResourceCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "apparatus_resource_capabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    apparatus_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capability_tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apparatus_resource_capabilities", x => x.id);
                    table.ForeignKey(
                        name: "FK_apparatus_resource_capabilities_apparatus_apparatus_id",
                        column: x => x.apparatus_id,
                        principalTable: "apparatus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_resource_capabilities_apparatus_id",
                table: "apparatus_resource_capabilities",
                column: "apparatus_id");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_resource_capabilities_apparatus_id_resource_type",
                table: "apparatus_resource_capabilities",
                columns: new[] { "apparatus_id", "resource_type" },
                unique: true,
                filter: "capability_tag IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_resource_capabilities_apparatus_id_resource_type_~",
                table: "apparatus_resource_capabilities",
                columns: new[] { "apparatus_id", "resource_type", "capability_tag" },
                unique: true,
                filter: "capability_tag IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_resource_capabilities_resource_type",
                table: "apparatus_resource_capabilities",
                column: "resource_type");

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_resource_capabilities_resource_type_capability_tag",
                table: "apparatus_resource_capabilities",
                columns: new[] { "resource_type", "capability_tag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "apparatus_resource_capabilities");

        }
    }
}
