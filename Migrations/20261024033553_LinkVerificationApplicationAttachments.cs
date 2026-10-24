using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class LinkVerificationApplicationAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "document_library_file_id",
                table: "verification_application_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sha256",
                table: "verification_application_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "validation_field_definitions",
                keyColumn: "id",
                keyValue: new Guid("4afd4103-2f12-168f-d6c8-d3b76eba201b"),
                column: "design_note",
                value: "Files are persisted with the application and linked to the application document folder on submit.");

            migrationBuilder.CreateIndex(
                name: "IX_verification_application_files_document_library_file_id",
                table: "verification_application_files",
                column: "document_library_file_id",
                unique: true,
                filter: "document_library_file_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_verification_application_files_document_files_document_libr~",
                table: "verification_application_files",
                column: "document_library_file_id",
                principalTable: "document_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_verification_application_files_document_files_document_libr~",
                table: "verification_application_files");

            migrationBuilder.DropIndex(
                name: "IX_verification_application_files_document_library_file_id",
                table: "verification_application_files");

            migrationBuilder.DropColumn(
                name: "document_library_file_id",
                table: "verification_application_files");

            migrationBuilder.DropColumn(
                name: "sha256",
                table: "verification_application_files");

            migrationBuilder.UpdateData(
                table: "validation_field_definitions",
                keyColumn: "id",
                keyValue: new Guid("4afd4103-2f12-168f-d6c8-d3b76eba201b"),
                column: "design_note",
                value: "Phase 1 preview only; no file is persisted.");
        }
    }
}
