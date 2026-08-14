using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTestCatalogCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_groups", x => x.id);
                    table.CheckConstraint("ck_equipment_groups_status", "status IN ('Active', 'Disabled')");
                });

            migrationBuilder.CreateTable(
                name: "report_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    template_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    template_file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    result_schema = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_templates", x => x.id);
                    table.CheckConstraint("ck_report_templates_status", "status IN ('Draft', 'Published', 'Retired')");
                    table.CheckConstraint("ck_report_templates_type", "template_type IN ('Excel', 'PDF', 'Other')");
                });

            migrationBuilder.CreateTable(
                name: "test_capabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_capabilities", x => x.id);
                    table.CheckConstraint("ck_test_capabilities_status", "status IN ('Draft', 'Active', 'Retired')");
                });

            migrationBuilder.CreateTable(
                name: "test_environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    site = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    booking_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_environments", x => x.id);
                    table.CheckConstraint("ck_test_environments_booking_mode", "booking_mode IN ('Exclusive', 'Shared')");
                    table.CheckConstraint("ck_test_environments_status", "status IN ('Active', 'Maintenance', 'Disabled')");
                });

            migrationBuilder.CreateTable(
                name: "test_plan_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    structured_definition = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_plan_templates", x => x.id);
                    table.CheckConstraint("ck_test_plan_templates_status", "status IN ('Draft', 'Published', 'Retired')");
                });

            migrationBuilder.CreateTable(
                name: "equipment_group_requirements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capability_tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    allow_alternative = table.Column<bool>(type: "boolean", nullable: false),
                    preferred_equipment_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_group_requirements", x => x.id);
                    table.CheckConstraint("ck_equipment_group_requirements_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_equipment_group_requirements_apparatus_preferred_equipment_~",
                        column: x => x.preferred_equipment_id,
                        principalTable: "apparatus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_equipment_group_requirements_equipment_groups_equipment_gro~",
                        column: x => x.equipment_group_id,
                        principalTable: "equipment_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_execution_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    test_capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_plan_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    estimated_duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    automation_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_execution_profiles", x => x.id);
                    table.CheckConstraint("ck_test_execution_profiles_automation", "automation_level IN ('Manual', 'SemiAuto', 'Auto')");
                    table.CheckConstraint("ck_test_execution_profiles_duration", "estimated_duration_minutes > 0");
                    table.CheckConstraint("ck_test_execution_profiles_status", "status IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "FK_test_execution_profiles_equipment_groups_equipment_group_id",
                        column: x => x.equipment_group_id,
                        principalTable: "equipment_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_execution_profiles_report_templates_report_template_id",
                        column: x => x.report_template_id,
                        principalTable: "report_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_execution_profiles_test_capabilities_test_capability_id",
                        column: x => x.test_capability_id,
                        principalTable: "test_capabilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_execution_profiles_test_environments_test_environment_~",
                        column: x => x.test_environment_id,
                        principalTable: "test_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_execution_profiles_test_plan_templates_test_plan_templ~",
                        column: x => x.test_plan_template_id,
                        principalTable: "test_plan_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "planned_test_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_execution_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_plan_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_plan_template_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    report_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_template_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    estimated_duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    planning_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_test_items", x => x.id);
                    table.CheckConstraint("ck_planned_test_items_duration", "estimated_duration_minutes > 0");
                    table.CheckConstraint("ck_planned_test_items_source", "planning_source IN ('Agent', 'Manual')");
                    table.CheckConstraint("ck_planned_test_items_status", "status IN ('Draft', 'WaitingResource', 'Ready', 'Running', 'Completed', 'Returned', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_planned_test_items_equipment_groups_equipment_group_id",
                        column: x => x.equipment_group_id,
                        principalTable: "equipment_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_module_records_module_record_id",
                        column: x => x.module_record_id,
                        principalTable: "module_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_report_templates_report_template_id",
                        column: x => x.report_template_id,
                        principalTable: "report_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_test_capabilities_test_capability_id",
                        column: x => x.test_capability_id,
                        principalTable: "test_capabilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_test_environments_test_environment_id",
                        column: x => x.test_environment_id,
                        principalTable: "test_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_test_execution_profiles_test_execution_p~",
                        column: x => x.test_execution_profile_id,
                        principalTable: "test_execution_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planned_test_items_test_plan_templates_test_plan_template_id",
                        column: x => x.test_plan_template_id,
                        principalTable: "test_plan_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_group_requirements_equipment_group_id",
                table: "equipment_group_requirements",
                column: "equipment_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_group_requirements_preferred_equipment_id",
                table: "equipment_group_requirements",
                column: "preferred_equipment_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_groups_code",
                table: "equipment_groups",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_equipment_group_id",
                table: "planned_test_items",
                column: "equipment_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_module_record_id_status",
                table: "planned_test_items",
                columns: new[] { "module_record_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_report_template_id",
                table: "planned_test_items",
                column: "report_template_id");

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_test_capability_id",
                table: "planned_test_items",
                column: "test_capability_id");

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_test_environment_id",
                table: "planned_test_items",
                column: "test_environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_test_execution_profile_id",
                table: "planned_test_items",
                column: "test_execution_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_planned_test_items_test_plan_template_id",
                table: "planned_test_items",
                column: "test_plan_template_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_templates_code_version",
                table: "report_templates",
                columns: new[] { "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_templates_status",
                table: "report_templates",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_test_capabilities_category_status",
                table: "test_capabilities",
                columns: new[] { "category", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_test_capabilities_code",
                table: "test_capabilities",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_environments_category_status",
                table: "test_environments",
                columns: new[] { "category", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_test_environments_code",
                table: "test_environments",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_code",
                table: "test_execution_profiles",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_equipment_group_id",
                table: "test_execution_profiles",
                column: "equipment_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_report_template_id",
                table: "test_execution_profiles",
                column: "report_template_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_test_capability_id_status",
                table: "test_execution_profiles",
                columns: new[] { "test_capability_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_test_capability_id",
                table: "test_execution_profiles",
                column: "test_capability_id",
                unique: true,
                filter: "status = 'Active' AND is_default");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_test_environment_id",
                table: "test_execution_profiles",
                column: "test_environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_execution_profiles_test_plan_template_id",
                table: "test_execution_profiles",
                column: "test_plan_template_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_templates_code_version",
                table: "test_plan_templates",
                columns: new[] { "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_plan_templates_status",
                table: "test_plan_templates",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_group_requirements");

            migrationBuilder.DropTable(
                name: "planned_test_items");

            migrationBuilder.DropTable(
                name: "test_execution_profiles");

            migrationBuilder.DropTable(
                name: "equipment_groups");

            migrationBuilder.DropTable(
                name: "report_templates");

            migrationBuilder.DropTable(
                name: "test_capabilities");

            migrationBuilder.DropTable(
                name: "test_environments");

            migrationBuilder.DropTable(
                name: "test_plan_templates");

        }
    }
}
