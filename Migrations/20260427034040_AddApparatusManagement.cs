using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddApparatusManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ apparatus 主表
            migrationBuilder.CreateTable(
                name: "apparatus",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductsId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Custodian = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReservationStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apparatus", x => x.Id);
                });

            // ✅ apparatus_files
            migrationBuilder.CreateTable(
                name: "apparatus_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApparatusId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadEmp = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apparatus_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_apparatus_files_apparatus_ApparatusId",
                        column: x => x.ApparatusId,
                        principalTable: "apparatus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apparatus_files_ApparatusId",
                table: "apparatus_files",
                column: "ApparatusId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "apparatus_files");

            migrationBuilder.DropTable(
                name: "apparatus");
        }
    }
}
