using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "verification_application_no_seq");

            migrationBuilder.CreateTable(
                name: "verification_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_no = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    module_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    applicant_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    applicant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    applicant_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    applicant_extension = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    project_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sub_pu = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    customer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    product_model = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    requested_finish_date = table.Column<DateOnly>(type: "date", nullable: true),
                    validation_requirement = table.Column<string>(type: "text", nullable: true),
                    hardware_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    firmware_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    software_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sample_ready_date = table.Column<DateOnly>(type: "date", nullable: true),
                    jira_link = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    location = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    npi = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    wireless_drive = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    chipset = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sample_mac_address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    utility_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    dsp_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    module_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    returned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    processing_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_applications", x => x.id);
                    table.CheckConstraint("ck_verification_applications_status", "status IN ('Draft', 'Submitted', 'Returned', 'Accepted', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_verification_applications_module_records_module_record_id",
                        column: x => x.module_record_id,
                        principalTable: "module_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "verification_application_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    verification_application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_application_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_verification_application_files_verification_applications_ve~",
                        column: x => x.verification_application_id,
                        principalTable: "verification_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_verification_application_files_verification_application_id",
                table: "verification_application_files",
                column: "verification_application_id");

            migrationBuilder.CreateIndex(
                name: "IX_verification_applications_application_no",
                table: "verification_applications",
                column: "application_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_applications_module_record_id",
                table: "verification_applications",
                column: "module_record_id",
                unique: true,
                filter: "module_record_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_verification_applications_status_submitted_at",
                table: "verification_applications",
                columns: new[] { "status", "submitted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_application_files");

            migrationBuilder.DropTable(
                name: "verification_applications");

            migrationBuilder.DropSequence(
                name: "verification_application_no_seq");
        }
    }
}
