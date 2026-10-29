using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentSafeDeletionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    equipment_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    products_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    storage_folder_name = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    attachment_count = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    db_record_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cleanup_attempt_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_deletion_jobs", x => x.id);
                    table.CheckConstraint("ck_equipment_deletion_status", "status IN ('WaitingForFiles','Finalizing','CleanupFailed','Blocked','Completed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_deletion_jobs_equipment_id",
                table: "equipment_deletion_jobs",
                column: "equipment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equipment_deletion_jobs_status_updated_at",
                table: "equipment_deletion_jobs",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'csit_runtime_dev') THEN
                        GRANT SELECT, INSERT, UPDATE ON TABLE equipment_deletion_jobs TO csit_runtime_dev;
                        REVOKE DELETE ON TABLE equipment_deletion_jobs FROM csit_runtime_dev;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_deletion_jobs");
        }
    }
}
