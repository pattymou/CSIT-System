using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddValidationTestRequestPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "validation_form_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_form_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "validation_product_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_product_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "validation_form_template_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_form_template_versions", x => x.id);
                    table.CheckConstraint("ck_validation_template_version_status", "status IN ('Draft','Published','Inactive')");
                    table.ForeignKey(
                        name: "FK_validation_form_template_versions_validation_form_templates~",
                        column: x => x.template_id,
                        principalTable: "validation_form_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_section_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    always_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_section_definitions", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_section_definitions_validation_form_template_ver~",
                        column: x => x.template_version_id,
                        principalTable: "validation_form_template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_test_item_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    display_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    always_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_test_item_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_test_item_categories_validation_form_template_ve~",
                        column: x => x.template_version_id,
                        principalTable: "validation_form_template_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_field_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_field_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    help_text = table.Column<string>(type: "text", nullable: true),
                    design_note = table.Column<string>(type: "text", nullable: true),
                    field_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    data_source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    validation_json = table.Column<string>(type: "jsonb", nullable: true),
                    was_published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_field_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_validation_fields_parent",
                        column: x => x.parent_field_definition_id,
                        principalTable: "validation_field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_validation_fields_section",
                        column: x => x.section_definition_id,
                        principalTable: "validation_section_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_product_type_section_mappings",
                columns: table => new
                {
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_definition_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_product_type_section_mappings", x => new { x.product_type_id, x.section_definition_id });
                    table.ForeignKey(
                        name: "FK_validation_product_type_section_mappings_validation_product~",
                        column: x => x.product_type_id,
                        principalTable: "validation_product_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_validation_product_type_section_mappings_validation_section~",
                        column: x => x.section_definition_id,
                        principalTable: "validation_section_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_product_type_test_category_mappings",
                columns: table => new
                {
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_item_category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_product_type_test_category_mappings", x => new { x.product_type_id, x.test_item_category_id });
                    table.ForeignKey(
                        name: "FK_validation_product_type_test_category_mappings_validation_p~",
                        column: x => x.product_type_id,
                        principalTable: "validation_product_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_validation_product_type_test_category_mappings_validation_t~",
                        column: x => x.test_item_category_id,
                        principalTable: "validation_test_item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_test_item_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_item_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    design_note = table.Column<string>(type: "text", nullable: true),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    was_published = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_test_item_definitions", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_test_item_definitions_validation_test_item_categ~",
                        column: x => x.test_item_category_id,
                        principalTable: "validation_test_item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_field_conditions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_field_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "character varying(30)", maxLength: 30, nullable: false),
                    expected_value = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_field_conditions", x => x.id);
                    table.CheckConstraint("ck_validation_condition_action", "action IN ('Visible','Required')");
                    table.CheckConstraint("ck_validation_condition_operator", "operator IN ('Equals','NotEquals','Contains','NotContains','IsTrue','IsFalse')");
                    table.ForeignKey(
                        name: "FK_validation_field_conditions_validation_field_definitions_ta~",
                        column: x => x.target_field_definition_id,
                        principalTable: "validation_field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_field_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    was_published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_field_options", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_field_options_validation_field_definitions_field~",
                        column: x => x.field_definition_id,
                        principalTable: "validation_field_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_test_item_parameter_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    test_item_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_parameter_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    help_text = table.Column<string>(type: "text", nullable: true),
                    design_note = table.Column<string>(type: "text", nullable: true),
                    field_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    validation_json = table.Column<string>(type: "jsonb", nullable: true),
                    was_published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_test_item_parameter_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_validation_parameters_item",
                        column: x => x.test_item_definition_id,
                        principalTable: "validation_test_item_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_validation_parameters_parent",
                        column: x => x.parent_parameter_definition_id,
                        principalTable: "validation_test_item_parameter_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "validation_test_item_parameter_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parameter_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    was_published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_test_item_parameter_options", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_test_item_parameter_options_validation_test_item~",
                        column: x => x.parameter_definition_id,
                        principalTable: "validation_test_item_parameter_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "validation_form_templates",
                columns: new[] { "id", "code", "created_at", "display_name", "updated_at" },
                values: new object[] { new Guid("a4f67945-6448-0c20-7c6d-c60ae0a48db3"), "VALIDATION_TEST_REQUEST", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "驗證測試申請單", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "validation_product_types",
                columns: new[] { "id", "code", "created_at", "display_label", "display_order", "is_enabled", "updated_at" },
                values: new object[,]
                {
                    { new Guid("0a70a7c3-4d8a-4e94-58ab-69bc453d84c2"), "FIVE_G_DONGLE_NO_WIFI", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "5G Dongle w/o Wi-Fi", 60, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("12954a8c-5dab-8806-9efb-e825d974e250"), "O_RU", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "O-RU", 80, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("12d3d5ca-ae8d-b8d0-ce5f-0372c84a4c4f"), "WIFI_AP", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "Wi-Fi AP", 10, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("38b80c35-f5a2-a150-fc60-67d3be62a315"), "FIVE_G_ODU", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "5G ODU", 40, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("79ba7d91-8cd7-d5c9-cbb9-adc2d2395405"), "FIVE_G_FWA", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "5G FWA", 30, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("d903749e-5f1e-1af0-7162-fbbeb0db7539"), "FIVE_G_DONGLE_WIFI", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "5G Dongle w/ Wi-Fi", 50, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("d95284f6-e7e8-1dc7-6813-a960d14fd2c1"), "SMALLCELL", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "SmallCell", 70, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("f730d7a3-869e-3245-65ed-1be8026b044f"), "WIFI_STATION", new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), "Wi-Fi Station", 20, true, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "validation_form_template_versions",
                columns: new[] { "id", "created_at", "published_at", "status", "template_id", "updated_at", "version_number" },
                values: new object[] { new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5"), new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), null, "Draft", new Guid("a4f67945-6448-0c20-7c6d-c60ae0a48db3"), new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), 1 });

            migrationBuilder.InsertData(
                table: "validation_section_definitions",
                columns: new[] { "id", "always_visible", "code", "description", "display_label", "display_order", "is_enabled", "template_version_id" },
                values: new object[,]
                {
                    { new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), true, "COMMON_PRODUCT_INFO", null, "共通產品資訊", 10, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), false, "TYPE_WIFI", null, "Wi-Fi", 20, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), false, "TYPE_CELLULAR_UE", null, "Cellular UE", 30, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), false, "TYPE_SMALLCELL", null, "SmallCell", 40, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") }
                });

            migrationBuilder.InsertData(
                table: "validation_test_item_categories",
                columns: new[] { "id", "always_visible", "code", "display_label", "display_order", "is_enabled", "template_version_id" },
                values: new object[,]
                {
                    { new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false, "D", "SmallCell", 40, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false, "B", "Wi-Fi", 20, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("7df46be4-08fb-9ddf-3a94-9ad8ad2bf3ef"), true, "A", "Sample Release Test", 10, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") },
                    { new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false, "C", "Cellular UE", 30, true, new Guid("358cbb10-3dbf-071b-c559-f5ae86e44db5") }
                });

            migrationBuilder.InsertData(
                table: "validation_field_definitions",
                columns: new[] { "id", "code", "data_source", "design_note", "display_label", "display_order", "field_type", "help_text", "is_enabled", "is_required", "is_sensitive", "parent_field_definition_id", "section_definition_id", "validation_json", "was_published" },
                values: new object[,]
                {
                    { new Guid("0a109b02-2443-9629-86a6-e95217345803"), "SMALLCELL_MODEM_DRIVER", null, null, "LTE/5G Modem Driver Version", 20, "Text", null, true, true, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("10965806-7c99-3179-33ed-37652996ab0b"), "APN_DATA", null, null, "APN - Data", 100, "RepeatableText", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("111ba2c6-d3c5-7ea2-a978-152d0a25b059"), "CPU_MODEL", null, null, "CPU Model Name", 140, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("12ca0b7e-f518-7837-459f-5c48650636b5"), "CELLULAR_MODEM_DRIVER", null, null, "LTE/5G Modem Driver Version", 20, "Text", null, true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("1408e0d3-19e2-cad0-fedc-5f366062d82d"), "FR2_BANDS", null, null, "FR2 Bands", 90, "Text", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("1c88608d-7530-43a1-d732-182688cad19e"), "WIFI_CHIPSET_24", null, null, "Wi-Fi Chipset Model Name (2.4GHz)", 20, "Text", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("1d3f5925-6d3a-6e47-81b7-6e13f4495185"), "DEFAULT_USERNAME", null, null, "Username", 190, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("207e5013-0c78-e3ba-250f-18fb72ace81c"), "CA_DC_COMBINATIONS", null, null, "CA/DC Band Combinations", 70, "Attachment", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("21100065-7d79-5e2e-ff90-a57ae5e8378b"), "DEFAULT_PASSWORD", null, null, "Password", 200, "SensitiveText", null, true, true, true, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("2b0185f6-0b66-b20a-abf0-6e387935676b"), "DEPARTMENT_CODE", null, null, "Department Code", 80, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("2b290376-f939-cce1-2aea-acb48711ed08"), "PJM", null, null, "PJM", 110, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("2e8061bc-ae88-4fa1-71e0-41986025410c"), "WIFI_CHIPSET_6", null, null, "Wi-Fi Chipset Model Name (6GHz)", 40, "Text", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), "THREE_GPP_RELEASE", null, null, "3GPP Release", 30, "Dropdown", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("3a9650ae-ed8b-ce1b-17f6-15e05ec7f6de"), "MANAGEMENT_PORT", null, null, "管理埠 Ethernet", 110, "SingleSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("3b6b833a-e8b5-7175-6d1a-f685be59beac"), "HARDWARE_ENGINEER", null, null, "Hardware Engineer", 90, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("3bd20eb7-51ec-de95-02ba-a56c5ccbdff1"), "AT_COMMAND", null, null, "AT Command", 110, "TextArea", "Instructions to access AT command", true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("43e2d3da-185b-0a63-1ff9-81898e4f8ad7"), "LOCATION", null, null, "Location", 10, "SingleSelect", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("480b7664-8a14-5a40-6d72-9471faf18afa"), "ESIM_SUPPORT", null, null, "eSIM Support (if applicable)", 120, "TextArea", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("4afd4103-2f12-168f-d6c8-d3b76eba201b"), "OTHER_PRODUCT_INFO", null, "Phase 1 preview only; no file is persisted.", "上傳其他產品資訊 (PRD/HW Diagram/SPEC/Pre-test Report)", 160, "Attachment", null, true, false, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("4f9c1d8f-8359-6074-8729-ba3f7d132b20"), "POWER_CONSUMPTION", null, null, "Power Consumption", 90, "SingleSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("53536e3d-6421-1997-7ee6-8b33bf64a8ce"), "WIFI_BAND", null, null, "Wi-Fi Band", 80, "MultiSelect", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("62b0a1b7-b72d-f8e2-b906-3381616e276c"), "WIFI_CHIPSET_5", null, null, "Wi-Fi Chipset Model Name (5GHz)", 30, "Text", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("6e64a8f2-7ad6-43a8-d25d-b5053067d06c"), "NR_BANDS", null, null, "5G NR Bands", 50, "Text", null, true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("71473b8b-8fd7-a3b0-82c2-c88d5b4b2d0d"), "DUPLEX", null, null, "Duplex／TDD 配置能力", 30, "MultiSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("720204fb-50ae-a5ab-b5cd-f099c82460b1"), "PRODUCT_TYPE", "ValidationProductType", null, "Product Type", 20, "Dropdown", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("7268c576-93ed-07e1-7447-b9d082caf686"), "MODEL_NAME", null, null, "Model Name (Askey Internal)", 30, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("74548ab9-989a-2738-f991-df2992ac85d6"), "LED_DISPLAY", null, null, "LED 顯示", 120, "SingleSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("7b12640f-1371-1216-fa70-070635041d4b"), "SMALLCELL_CHIPSET", null, null, "LTE/5G Chipset Model Name", 10, "Text", null, true, true, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("7c8a7caa-062f-21e5-31dc-6ce05e8d0011"), "FW_SW_VERSION", null, null, "FW/SW Version", 130, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("8260b0df-c02c-c449-f427-bb5437b4c244"), "FR2_SUPPORTED", null, null, "Frequency Range 2 (FR2)", 80, "Checkbox", null, true, false, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("8397c88e-da02-cb79-b1cd-7df4f14014ed"), "LTE_BANDS", null, null, "4G LTE Bands", 60, "Text", null, true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("83e2c96d-6cac-6502-81e4-736156cd28eb"), "CELLULAR_CHIPSET", null, null, "LTE/5G Chipset Model Name", 10, "Text", null, true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("84d4a306-80a3-5661-af1b-21ab34fa44f9"), "DEFAULT_IP_VALUE", null, null, "IP", 180, "Text", null, true, false, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("87772a89-4c86-f65d-cc32-363b2efe1583"), "CHANNEL_6", null, null, "Supported Channel 6GHz", 130, "Group", null, true, false, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("8b79ef2c-0447-0d1a-9210-2de074f54068"), "MAXIMUM_UE", null, null, "Maximum UE", 140, "Dropdown", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("8f909c8a-5732-fa1a-eb79-9c0ddb994ef1"), "WIFI_ROLE", null, null, "Wi-Fi Role", 60, "Dropdown", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("901a39b1-9791-ade5-3435-b506521bd452"), "ETHERNET_PORTS", null, "Each interface has a speed/type and Port Count.", "Ethernet / SFP+ Port Number (LAN)", 150, "RepeatableGroup", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("90d1c546-6ec2-3cf9-d587-3b8d2e4420b5"), "SPATIAL_STREAM", null, null, "Spatial Stream", 100, "Group", null, true, false, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("9300ab83-4978-49d8-e144-e6ff5f105189"), "SSH_LOGIN_INFO", null, null, "SSH/Telnet Login Info", 220, "Group", null, true, false, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("9661c311-cf11-f185-e243-961767734509"), "NPI_PHASE", null, null, "NPI Phase", 60, "Dropdown", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("9a5b58f1-62c4-7d52-09d5-de7e9d3afc26"), "CUSTOMER", "SystemOption:Customer", null, "Customer Name / Code", 50, "Lookup", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("9f028b05-8d84-cc5c-bcb2-a12fe602aab8"), "CHANNEL_24", null, null, "Supported Channel 2.4GHz", 110, "SingleSelect", null, true, false, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("a05b244b-3f6c-adbd-999b-cb71547dccbd"), "DEPLOYMENT", null, null, "部署型態", 150, "MultiSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("a9cfe659-f12b-555c-5b48-c1981a16f076"), "MAX_BANDWIDTH", null, null, "Max. Bandwidth", 90, "Group", null, true, false, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), "WIFI_MODE", null, null, "Wi-Fi Mode", 70, "Dropdown", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("ba972941-8970-eaf9-6974-278ae215f625"), "GPS_ANTENNA", null, null, "GPS Antenna", 70, "SingleSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("c74c018f-df94-c60c-5f31-4f71bf8ef164"), "WIFI_DRIVER_VERSION", null, null, "Wi-Fi Driver Version", 50, "Text", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), "SUPPORTED_RATS", null, null, "Supported RATs", 40, "MultiSelect", null, true, true, false, null, new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9"), null, false },
                    { new Guid("d2041597-5edb-0827-f98f-4baa22b6c2b6"), "BACKHAUL", null, null, "Backhaul Interface / Speed", 130, "Group", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("d5231574-cdd4-b903-201a-cf22746925b7"), "SYNC_SOURCE", null, null, "同步來源", 100, "MultiSelect", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("dc6d955d-4ac6-e2fe-367f-c743e6d67bee"), "DEFAULT_IP_MODE", null, null, "Default IP", 170, "SingleSelect", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("dd69c28a-1458-1a58-9a93-4c8b40c47bcc"), "PRODUCT_NAME", null, null, "Product Name (Customer)", 40, "Text", null, true, false, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("e6ced83a-68df-e52f-6c01-dc70436c19d6"), "HW_VERSION", null, null, "HW Version", 120, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("e87dcdf7-0785-c4a3-6b9b-4c3b7ceb7c0e"), "SSH_TELNET", null, null, "SSH/Telnet", 210, "SingleSelect", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("eb8ec6b3-9a06-3f4b-5458-8cfd1d3fa03e"), "WIFI_CHIPSET_MAIN", null, null, "Wi-Fi Chipset Model Name (Main)", 10, "Text", null, true, true, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), "PU_CODE", null, null, "PU Code", 70, "Dropdown", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), "CHANNEL_5", null, null, "Supported Channel 5GHz", 120, "MultiSelect", null, true, false, false, null, new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("f5882157-fb66-9a22-058d-ec2f37ae2bdc"), "SOFTWARE_ENGINEER", null, null, "Software Engineer", 100, "Text", null, true, true, false, null, new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("f5de739b-c374-fca0-9303-3d0e9a48a136"), "CELL_IDENTITY", null, null, "Cell identity 能力", 40, "Text", "PCI、Cell ID、TAC、PLMN 等可設定範圍。", true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("f605126e-0eba-3a85-18c3-afdedb2f0bea"), "POWER_SUPPLY", null, null, "電源供應", 80, "Group", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("fa039290-6b11-12db-b4e8-da038056a045"), "RF_ANTENNA_CONFIG", null, null, "RF 天線配置", 60, "Group", null, true, false, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("fb5bdaa9-963b-bdb1-7c0d-933eaf5dd18b"), "RF_POWER_ANTENNA", null, null, "RF power／天線配置", 50, "Group", null, true, true, false, null, new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false }
                });

            migrationBuilder.InsertData(
                table: "validation_product_type_section_mappings",
                columns: new[] { "product_type_id", "section_definition_id" },
                values: new object[,]
                {
                    { new Guid("0a70a7c3-4d8a-4e94-58ab-69bc453d84c2"), new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9") },
                    { new Guid("12954a8c-5dab-8806-9efb-e825d974e250"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf") },
                    { new Guid("12d3d5ca-ae8d-b8d0-ce5f-0372c84a4c4f"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba") },
                    { new Guid("38b80c35-f5a2-a150-fc60-67d3be62a315"), new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9") },
                    { new Guid("79ba7d91-8cd7-d5c9-cbb9-adc2d2395405"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba") },
                    { new Guid("79ba7d91-8cd7-d5c9-cbb9-adc2d2395405"), new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9") },
                    { new Guid("d903749e-5f1e-1af0-7162-fbbeb0db7539"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba") },
                    { new Guid("d903749e-5f1e-1af0-7162-fbbeb0db7539"), new Guid("8eaa7b56-4383-5380-8bde-f28d49c956a9") },
                    { new Guid("d95284f6-e7e8-1dc7-6813-a960d14fd2c1"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf") },
                    { new Guid("f730d7a3-869e-3245-65ed-1be8026b044f"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba") }
                });

            migrationBuilder.InsertData(
                table: "validation_product_type_test_category_mappings",
                columns: new[] { "product_type_id", "test_item_category_id" },
                values: new object[,]
                {
                    { new Guid("0a70a7c3-4d8a-4e94-58ab-69bc453d84c2"), new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3") },
                    { new Guid("12954a8c-5dab-8806-9efb-e825d974e250"), new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd") },
                    { new Guid("12d3d5ca-ae8d-b8d0-ce5f-0372c84a4c4f"), new Guid("661aa80e-0991-a2da-954d-1ff6c473f675") },
                    { new Guid("38b80c35-f5a2-a150-fc60-67d3be62a315"), new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3") },
                    { new Guid("79ba7d91-8cd7-d5c9-cbb9-adc2d2395405"), new Guid("661aa80e-0991-a2da-954d-1ff6c473f675") },
                    { new Guid("79ba7d91-8cd7-d5c9-cbb9-adc2d2395405"), new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3") },
                    { new Guid("d903749e-5f1e-1af0-7162-fbbeb0db7539"), new Guid("661aa80e-0991-a2da-954d-1ff6c473f675") },
                    { new Guid("d903749e-5f1e-1af0-7162-fbbeb0db7539"), new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3") },
                    { new Guid("d95284f6-e7e8-1dc7-6813-a960d14fd2c1"), new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd") },
                    { new Guid("f730d7a3-869e-3245-65ed-1be8026b044f"), new Guid("661aa80e-0991-a2da-954d-1ff6c473f675") }
                });

            migrationBuilder.InsertData(
                table: "validation_test_item_definitions",
                columns: new[] { "id", "code", "description", "design_note", "display_label", "display_order", "is_archived", "is_enabled", "is_mandatory", "test_item_category_id", "was_published" },
                values: new object[,]
                {
                    { new Guid("0dd67e7b-151a-03f7-5c2c-595631bb63bb"), "CELLULAR_ENDURANCE", null, null, "LTE/5G Endurance Test", 50, false, true, false, new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false },
                    { new Guid("144a72d7-8243-d6b5-70ae-1bb2b0932b9c"), "WIFI_OTA_THROUGHPUT", null, "Mandatory test item for each NPI phase", "Wi-Fi OTA Throughput Test", 20, false, true, true, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("2d74eba2-9656-cae5-7362-4d211488b093"), "SC_MULTIPLE_UE", null, null, "Multiple UE Test", 60, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("2dd97c15-dbdf-0b86-7706-e4dac150e09f"), "CELLULAR_UE_CAPABILITY", null, null, "LTE/5G UE Capability Test", 30, false, true, false, new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false },
                    { new Guid("42fc12eb-dbb3-e40a-50db-8979445c4d29"), "SC_HANDOVER", null, null, "Handover Test", 50, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("58392712-d7d2-2f9d-bc13-83a72a567890"), "WIFI_RVR", null, "Mandatory test item for each NPI phase", "Wi-Fi RvR Test", 30, false, true, true, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("72eae84d-c1e1-b854-e675-c7c2358b46c7"), "WIFI_INDOOR", null, null, "Wi- Fi Indoor Test", 60, false, true, false, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("98677e28-bb28-ef1d-4208-503c85acc957"), "WIFI_TRP_TIS", null, "Mandatory test item for each NPI phase", "Wi-Fi TRP/TIS Test", 10, false, true, true, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("9b47b7ca-5d7b-e0ec-7bec-2fcf51ec4db0"), "WIFI_COVERAGE", null, null, "Wi-Fi Coverage Test", 50, false, true, false, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("a776c89e-edf2-0995-d13c-ee7b0f17037b"), "SC_STABILITY", null, null, "Stability Test", 90, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("afe6c4ca-c20e-3bbe-713a-17bf52584c03"), "CELLULAR_TRP_TIS", null, null, "LTE/5G TRP/TIS Test", 10, false, true, true, new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false },
                    { new Guid("b2ab3cfb-76f8-2335-8e17-0bbbed09ee90"), "SC_FUNCTIONAL", null, null, "Functional Test", 100, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("b30648c6-68ee-5825-0a13-9e3a135fc83a"), "CELLULAR_OTA_THROUGHPUT", null, null, "LTE/5G OTA Throughput Test", 20, false, true, true, new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false },
                    { new Guid("c3a83464-1cbf-6419-a9d5-e84372a4052d"), "SC_AMP_INTEROPERABILITY", null, null, "AMP Interoperibility Test", 30, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("c4cd2ed5-5ac9-3f66-2039-9bb4520c208c"), "SC_INDOOR_COVERAGE", null, null, "Indoor Coverage Test", 70, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("c77e5b92-8132-b910-5383-444e58441815"), "CELLULAR_BAND_SELECTION", null, null, "LTE/5G Band Selection Test", 40, false, true, false, new Guid("8f799cd1-113f-ddf7-15db-2497aaa886c3"), false },
                    { new Guid("d8114c17-3f11-85d8-9cc5-5f2d11029ef8"), "WIFI_ANGLE", null, null, "Wi-Fi Angle Test", 40, false, true, false, new Guid("661aa80e-0991-a2da-954d-1ff6c473f675"), false },
                    { new Guid("e5572012-d04e-d8fd-d9f9-c86690b545c1"), "SC_OTA_THROUGHPUT", null, null, "OTA Throughput Test", 10, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("ef79cb97-c253-b392-8e24-3152a941f35e"), "SC_RATE_VS_RANGE", null, null, "Rate vs Range Test", 20, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("f3dd3327-98ec-3465-03f0-6c32b9745c91"), "SC_THERMAL", null, null, "Thermal Test", 40, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false },
                    { new Guid("fc74b774-6766-fdad-5ced-312a01c5e3cc"), "SC_OUTDOOR_COVERAGE", null, null, "Outdoor Coverage Test", 80, false, true, false, new Guid("3ff3cb2a-8dce-330b-d259-00f10c2bddcd"), false }
                });

            migrationBuilder.InsertData(
                table: "validation_field_conditions",
                columns: new[] { "id", "action", "expected_value", "operator", "source_field_code", "target_field_definition_id" },
                values: new object[,]
                {
                    { new Guid("4cca1ccd-cb65-3939-b65c-3055ffadc7f4"), "Required", "FIXED_IP", "Equals", "DEFAULT_IP_MODE", new Guid("84d4a306-80a3-5661-af1b-21ab34fa44f9") },
                    { new Guid("4ce2249c-b443-19ef-1b6b-dbda14845de1"), "Visible", "BAND_24", "Contains", "WIFI_BAND", new Guid("9f028b05-8d84-cc5c-bcb2-a12fe602aab8") },
                    { new Guid("658a5fa5-d1b3-517a-5a96-0dacb23ed8e8"), "Required", "ENABLE", "Equals", "SSH_TELNET", new Guid("9300ab83-4978-49d8-e144-e6ff5f105189") },
                    { new Guid("b1ca3742-22fd-68a4-b410-e52e31443156"), "Visible", "BAND_6", "Contains", "WIFI_BAND", new Guid("87772a89-4c86-f65d-cc32-363b2efe1583") },
                    { new Guid("b2c771bd-df6c-6711-657d-4222eba18034"), "Visible", "BAND_5", "Contains", "WIFI_BAND", new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8") },
                    { new Guid("c715f59c-b631-34cc-b584-375a62ca6669"), "Required", "BAND_5", "Contains", "WIFI_BAND", new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8") },
                    { new Guid("d151d75c-1487-275a-0920-d6e031614472"), "Required", "BAND_24", "Contains", "WIFI_BAND", new Guid("9f028b05-8d84-cc5c-bcb2-a12fe602aab8") },
                    { new Guid("d78b0315-844e-f8ff-c7dc-6f5a55daa2d0"), "Visible", null, "IsTrue", "FR2_SUPPORTED", new Guid("1408e0d3-19e2-cad0-fedc-5f366062d82d") },
                    { new Guid("e469c005-6cd6-27b9-487e-58e3c6915fa1"), "Visible", "FIXED_IP", "Equals", "DEFAULT_IP_MODE", new Guid("84d4a306-80a3-5661-af1b-21ab34fa44f9") },
                    { new Guid("ee2b21c8-d1ec-b79b-ff9b-b6933b31000a"), "Required", "BAND_6", "Contains", "WIFI_BAND", new Guid("87772a89-4c86-f65d-cc32-363b2efe1583") },
                    { new Guid("f677b9c4-f585-674e-f29d-4260fe4eb405"), "Visible", "ENABLE", "Equals", "SSH_TELNET", new Guid("9300ab83-4978-49d8-e144-e6ff5f105189") }
                });

            migrationBuilder.InsertData(
                table: "validation_field_definitions",
                columns: new[] { "id", "code", "data_source", "design_note", "display_label", "display_order", "field_type", "help_text", "is_enabled", "is_required", "is_sensitive", "parent_field_definition_id", "section_definition_id", "validation_json", "was_published" },
                values: new object[,]
                {
                    { new Guid("20204a3b-d532-059f-7dc2-e474cf2202cc"), "ETHERNET_PORT_COUNT", null, null, "Port Count", 20, "Number", null, true, true, false, new Guid("901a39b1-9791-ade5-3435-b506521bd452"), new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), "{\"integer\":true,\"min\":1}", false },
                    { new Guid("212c0317-5676-adc3-cb73-b86c392b6790"), "POE_PLUS", null, null, "PoE+", 20, "SingleSelect", null, true, false, false, new Guid("f605126e-0eba-3a85-18c3-afdedb2f0bea"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("30a6e4e6-2ad1-67ba-84aa-f949d4de7f35"), "RF_MIMO", null, null, "MIMO", 10, "SingleSelect", null, true, false, false, new Guid("fa039290-6b11-12db-b4e8-da038056a045"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("3d17a3f1-fe6d-e0db-34d7-dd78744d2c45"), "MAX_BW_24", null, null, "2.4 GHz", 10, "MultiSelect", null, true, false, false, new Guid("a9cfe659-f12b-555c-5b48-c1981a16f076"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("48a9f008-ee0e-e71c-4059-cf47621813f1"), "BACKHAUL_FIBER", null, null, "Fiber", 20, "MultiSelect", null, true, false, false, new Guid("d2041597-5edb-0827-f98f-4baa22b6c2b6"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("53d77bdd-3fa0-6f9b-f0fd-d26763f0e1d4"), "SSH_USERNAME", null, null, "Username", 10, "Text", null, true, false, false, new Guid("9300ab83-4978-49d8-e144-e6ff5f105189"), new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("6084e3ae-45f1-7d13-0244-36d33f1eabbd"), "RF_BUILTIN", null, null, "Build-in RF Antenna", 20, "SingleSelect", null, true, false, false, new Guid("fa039290-6b11-12db-b4e8-da038056a045"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("70528943-a0e3-6a97-0556-b1bf5ae5a538"), "POWER_INPUT", null, null, "Power Input", 10, "MultiSelect", null, true, false, false, new Guid("f605126e-0eba-3a85-18c3-afdedb2f0bea"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("86723d31-4ed3-93dc-f405-df4b07947cb9"), "CHANNEL_6_FROM", null, null, "From CH", 10, "Number", null, true, false, false, new Guid("87772a89-4c86-f65d-cc32-363b2efe1583"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), "{\"integer\":true}", false },
                    { new Guid("92909068-7036-c50c-9ccb-72247035c92c"), "TX_POWER_MIN", null, null, "Tx Power Min", 20, "Number", null, true, true, false, new Guid("fb5bdaa9-963b-bdb1-7c0d-933eaf5dd18b"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), "{\"integer\":true,\"unit\":\"dB\"}", false },
                    { new Guid("94f4f5ec-d142-4b91-c42c-87624d182057"), "TX_POWER_MAX", null, null, "Tx Power Max", 10, "Number", null, true, true, false, new Guid("fb5bdaa9-963b-bdb1-7c0d-933eaf5dd18b"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), "{\"integer\":true,\"unit\":\"dB\"}", false },
                    { new Guid("9c147294-50f2-086b-0561-f52925e6eb0b"), "BACKHAUL_ETHERNET", null, null, "Ethernet", 10, "MultiSelect", null, true, false, false, new Guid("d2041597-5edb-0827-f98f-4baa22b6c2b6"), new Guid("9d976121-dbc7-370d-77bb-bbb4f2b0a5bf"), null, false },
                    { new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), "ETHERNET_INTERFACE", null, null, "Interface", 10, "Dropdown", null, true, true, false, new Guid("901a39b1-9791-ade5-3435-b506521bd452"), new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("a8e534fa-2d36-616e-c51b-db71e4281919"), "MAX_BW_6", null, null, "6 GHz", 30, "MultiSelect", null, true, false, false, new Guid("a9cfe659-f12b-555c-5b48-c1981a16f076"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("a9f4c5e6-6a0c-4798-7745-c313a1774e1e"), "SSH_PASSWORD", null, null, "Password", 20, "SensitiveText", null, true, false, true, new Guid("9300ab83-4978-49d8-e144-e6ff5f105189"), new Guid("0488d129-54e4-787d-1090-9a0be30e707a"), null, false },
                    { new Guid("ad49a370-510d-fce5-cd2d-2663910d34a2"), "MAX_BW_5", null, null, "5 GHz", 20, "MultiSelect", null, true, false, false, new Guid("a9cfe659-f12b-555c-5b48-c1981a16f076"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("d53353a7-9611-7085-9eb2-76c779b08598"), "CHANNEL_6_TO", null, null, "To CH", 20, "Number", null, true, false, false, new Guid("87772a89-4c86-f65d-cc32-363b2efe1583"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), "{\"integer\":true}", false },
                    { new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), "SPATIAL_STREAM_5", null, null, "5 GHz", 20, "Dropdown", null, true, false, false, new Guid("90d1c546-6ec2-3cf9-d587-3b8d2e4420b5"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), "SPATIAL_STREAM_6", null, null, "6 GHz", 30, "Dropdown", null, true, false, false, new Guid("90d1c546-6ec2-3cf9-d587-3b8d2e4420b5"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false },
                    { new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), "SPATIAL_STREAM_24", null, null, "2.4 GHz", 10, "Dropdown", null, true, false, false, new Guid("90d1c546-6ec2-3cf9-d587-3b8d2e4420b5"), new Guid("5046c7da-ac54-8732-d515-73dca8be1fba"), null, false }
                });

            migrationBuilder.InsertData(
                table: "validation_field_options",
                columns: new[] { "id", "display_label", "display_order", "field_definition_id", "is_enabled", "value_code", "was_published" },
                values: new object[,]
                {
                    { new Guid("05c8b803-a42c-00b9-fc98-d55fdf711baf"), "Wi-Fi 4", 10, new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), true, "WIFI_4", false },
                    { new Guid("098cdfb2-ec2d-ddef-fee8-3969406c16dd"), "1B", 20, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "1B", false },
                    { new Guid("0b753cf2-169a-6b38-e895-d6e72a51f6e1"), "Rel-16", 40, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "REL_16", false },
                    { new Guid("0f5bb2d7-f054-5f3d-540b-bc010561cf0b"), "2E", 50, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "2E", false },
                    { new Guid("0fa5e3f4-09db-3257-63ad-408db6b7fdf2"), "CH1-13", 20, new Guid("9f028b05-8d84-cc5c-bcb2-a12fe602aab8"), true, "CH1_13", false },
                    { new Guid("10bff968-b845-ac09-4dfc-e9fa43df9db4"), "RedCap", 40, new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), true, "REDCAP", false },
                    { new Guid("132e8e1c-aadc-78aa-b944-2bef93d71670"), "PV", 40, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "PV", false },
                    { new Guid("1666efa3-8415-e640-4ba3-d4b2b76b5573"), "6A", 120, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "6A", false },
                    { new Guid("17c8871b-3210-450e-152d-b5961324fa49"), "AP", 10, new Guid("8f909c8a-5732-fa1a-eb79-9c0ddb994ef1"), true, "AP", false },
                    { new Guid("21301497-e774-6b15-674e-48c577cccaf8"), "Both", 30, new Guid("8f909c8a-5732-fa1a-eb79-9c0ddb994ef1"), true, "BOTH", false },
                    { new Guid("22054a0c-eb9c-b1ad-92ca-daad9f6e608e"), "256", 40, new Guid("8b79ef2c-0447-0d1a-9210-2de074f54068"), true, "256", false },
                    { new Guid("22506063-8384-5c01-81cd-1c1817888278"), "ES", 10, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "ES", false },
                    { new Guid("2387cb47-05ce-0ec2-cd10-ca39c360f5df"), "No", 20, new Guid("3a9650ae-ed8b-ce1b-17f6-15e05ec7f6de"), true, "NO", false },
                    { new Guid("274a5a85-8c83-7b75-f3d3-4d50d4466a70"), "Fixed IP", 10, new Guid("dc6d955d-4ac6-e2fe-367f-c743e6d67bee"), true, "FIXED_IP", false },
                    { new Guid("342d4bb5-a640-f693-3b11-c888e1a517b2"), "CH52-64", 20, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH52_64", false },
                    { new Guid("3ad3291d-ac56-4b74-f3cb-78bbf312eb24"), "1A", 10, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "1A", false },
                    { new Guid("431e65bc-06d1-9080-48ea-737cb0708b5a"), "5A", 110, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "5A", false },
                    { new Guid("494e1749-476c-0d32-7dad-01b0e8b33dce"), "2M", 80, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "2M", false },
                    { new Guid("496859c0-0741-143b-8b2d-e139efcf9575"), "eRedCap", 50, new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), true, "EREDCAP", false },
                    { new Guid("4e4ba654-7a3e-0656-4a7e-10cd4ca23429"), "Yes", 10, new Guid("3a9650ae-ed8b-ce1b-17f6-15e05ec7f6de"), true, "YES", false },
                    { new Guid("4e65ad10-73f9-8c84-0d73-c22a8c819782"), "Indoor", 10, new Guid("a05b244b-3f6c-adbd-999b-cb71547dccbd"), true, "INDOOR", false },
                    { new Guid("4e6c9bc7-5517-6cfe-641d-f7b7d5695c9e"), "64", 20, new Guid("8b79ef2c-0447-0d1a-9210-2de074f54068"), true, "64", false },
                    { new Guid("5120bf9a-ff9a-eab1-5fa5-fd13cf71e963"), "250mW", 10, new Guid("4f9c1d8f-8359-6074-8729-ba3f7d132b20"), true, "250MW", false },
                    { new Guid("5157c702-f4cb-878f-e8d8-d0f0b2a76b4e"), "128", 30, new Guid("8b79ef2c-0447-0d1a-9210-2de074f54068"), true, "128", false },
                    { new Guid("58097416-9697-3ebd-732b-3f1798b9a20e"), "PTP", 20, new Guid("d5231574-cdd4-b903-201a-cf22746925b7"), true, "PTP", false },
                    { new Guid("5ade2535-3fbd-a36c-f910-6fa9e5d4183b"), "4G LTE", 10, new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), true, "LTE", false },
                    { new Guid("5be00fe2-89e5-30b3-74e0-458658ab39dd"), "FDD", 10, new Guid("71473b8b-8fd7-a3b0-82c2-c88d5b4b2d0d"), true, "FDD", false },
                    { new Guid("5e2eb00b-e34f-de15-b406-21fef148ef54"), "Rel-18", 20, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "REL_18", false },
                    { new Guid("6046d6d9-81b6-459f-5b71-771dc0f28006"), "1顆", 10, new Guid("74548ab9-989a-2738-f991-df2992ac85d6"), true, "ONE", false },
                    { new Guid("60688818-c60a-0988-e8d2-eceadbd19210"), "9A", 150, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "9A", false },
                    { new Guid("6311ba41-c62f-342c-541a-c6e04537a029"), "CH132-144", 50, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH132_144", false },
                    { new Guid("63cffd62-b9ff-ea11-e9ee-f1224939edc2"), "2G", 70, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "2G", false },
                    { new Guid("64c1f74c-183d-a22a-202a-f8a8c12950af"), "EV", 20, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "EV", false },
                    { new Guid("6757b8a7-918e-15c8-42d9-90b0cd514db6"), "Wi-Fi 7", 40, new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), true, "WIFI_7", false },
                    { new Guid("6880224f-48f4-10ef-0efc-8c8e6059bd6f"), "Outdoor", 20, new Guid("a05b244b-3f6c-adbd-999b-cb71547dccbd"), true, "OUTDOOR", false },
                    { new Guid("68de7784-3e22-68ef-fc4e-eb6f3ae8543b"), "CH149-161", 60, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH149_161", false },
                    { new Guid("6aad5743-d81f-0f7a-6dc6-e66f9981c765"), "Disable", 20, new Guid("e87dcdf7-0785-c4a3-6b9b-4c3b7ceb7c0e"), true, "DISABLE", false },
                    { new Guid("6d2b90ff-6582-9799-4a59-56c77d29d457"), "Rel-17", 30, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "REL_17", false },
                    { new Guid("6ed29b84-e9b9-a37e-d1f5-dfd05c29c381"), "CH100-112", 30, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH100_112", false },
                    { new Guid("74ff481b-bf66-e594-ded1-abf1e3f9cc0f"), "5G NR SA", 20, new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), true, "NR_SA", false },
                    { new Guid("79f4a693-4c16-dd8b-e263-b97672358135"), "Wujiang", 20, new Guid("43e2d3da-185b-0a63-1ff9-81898e4f8ad7"), true, "WUJIANG", false },
                    { new Guid("7c0bca92-45c2-3497-70fb-34e30c8c7e76"), "Wi-Fi 6", 30, new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), true, "WIFI_6", false },
                    { new Guid("7dbc7224-1869-e090-fa58-816cae0a9df0"), "2A", 40, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "2A", false },
                    { new Guid("7e6850d4-f17d-f7e0-f585-dc87befd7276"), "EN-DC", 30, new Guid("c7def696-6d8d-b5bb-f421-72ef1e8e0b9d"), true, "EN_DC", false },
                    { new Guid("7fbba2c4-31cd-6310-6c9a-eb2ea79ce3da"), "3A", 90, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "3A", false },
                    { new Guid("80e6b6e6-9555-41ef-7a8f-139a1b88af7a"), "5W", 20, new Guid("4f9c1d8f-8359-6074-8729-ba3f7d132b20"), true, "5W", false },
                    { new Guid("847527cd-2335-adda-86d5-81af4a24e1cf"), "6 GHz", 30, new Guid("53536e3d-6421-1997-7ee6-8b33bf64a8ce"), true, "BAND_6", false },
                    { new Guid("84960715-5160-8c38-8101-8a84a542e21e"), "32", 10, new Guid("8b79ef2c-0447-0d1a-9210-2de074f54068"), true, "32", false },
                    { new Guid("84a17984-924b-c0a0-55ef-4ca0896934ed"), "CH1-11", 10, new Guid("9f028b05-8d84-cc5c-bcb2-a12fe602aab8"), true, "CH1_11", false },
                    { new Guid("89ded2ce-5afc-7f7e-a9b5-57d6c129b3dc"), "6C", 140, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "6C", false },
                    { new Guid("8a8ea19e-5824-6b46-657c-69f453a6d173"), "Wi-Fi 5", 20, new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), true, "WIFI_5", false },
                    { new Guid("8c5bf132-368d-c4ee-968d-1d8a9c23f5de"), "4A", 100, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "4A", false },
                    { new Guid("8e2c0e5d-b7bb-e31f-0fbf-0238b4975afd"), "2F", 60, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "2F", false },
                    { new Guid("900e993c-f895-4e20-4d9f-054b6bc3c1e7"), "CH116-128", 40, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH116_128", false },
                    { new Guid("94d5081e-27ee-0a82-1a28-85dae3fe2f33"), "1C", 30, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "1C", false },
                    { new Guid("97a70f99-08e8-80be-2f17-6b0ab298d5f8"), "Enable", 10, new Guid("e87dcdf7-0785-c4a3-6b9b-4c3b7ceb7c0e"), true, "ENABLE", false },
                    { new Guid("a1dfe27e-0bcd-3b0f-bd99-95cc7ac3ee35"), "TDD", 20, new Guid("71473b8b-8fd7-a3b0-82c2-c88d5b4b2d0d"), true, "TDD", false },
                    { new Guid("a279de37-b998-0941-1222-562afa2b5414"), "Build-in", 10, new Guid("ba972941-8970-eaf9-6974-278ae215f625"), true, "BUILTIN", false },
                    { new Guid("a3655fee-2f24-3841-5db8-3e660dc19d9e"), "DV", 30, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "DV", false },
                    { new Guid("a74cb8b8-e1f1-70ab-7758-36c01bd69356"), "MP", 50, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "MP", false },
                    { new Guid("a7f5a41e-c6fc-dde5-8de9-3c9778327dec"), "External", 20, new Guid("ba972941-8970-eaf9-6974-278ae215f625"), true, "EXTERNAL", false },
                    { new Guid("a9e5b3ab-6af2-4d16-110a-7b8d1398a5a6"), "CH36-48", 10, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH36_48", false },
                    { new Guid("b3bbdab1-b790-b810-dee5-c23bc040832c"), "9H", 170, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "9H", false },
                    { new Guid("b72d41b6-c0e7-bec2-e913-7152a49d9453"), "2.4 GHz", 10, new Guid("53536e3d-6421-1997-7ee6-8b33bf64a8ce"), true, "BAND_24", false },
                    { new Guid("ba02c3fe-da66-055e-2282-d4310d2060c9"), "6B", 130, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "6B", false },
                    { new Guid("c0b094eb-3d76-08cd-fbcf-08bc3e1c1dc2"), "9G", 160, new Guid("ec9d04fc-b386-3eb0-c21b-b7646e83ada5"), true, "9G", false },
                    { new Guid("c395a240-f398-fdbe-31eb-92a0e285d2e8"), "Other", 60, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "OTHER", false },
                    { new Guid("c7d8d3a4-c7e3-1381-9948-821ee3c81732"), "Wi-Fi 8", 50, new Guid("b771bf05-de8a-11d7-19ba-b71c510221a3"), true, "WIFI_8", false },
                    { new Guid("c85d1628-c294-20e0-7053-be3f3e637650"), "GPS", 10, new Guid("d5231574-cdd4-b903-201a-cf22746925b7"), true, "GPS", false },
                    { new Guid("cf8d7a51-47df-addb-36a9-ac61ed41a127"), "Rel-19", 10, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "REL_19", false },
                    { new Guid("d542be2e-cbfc-6b23-1609-2402fba316d7"), "5 GHz", 20, new Guid("53536e3d-6421-1997-7ee6-8b33bf64a8ce"), true, "BAND_5", false },
                    { new Guid("d82b0ac7-2ca2-4e5c-8255-1ccf03ecf10e"), "其他", 60, new Guid("9661c311-cf11-f185-e243-961767734509"), true, "OTHER", false },
                    { new Guid("e1c07d72-2dc4-7e2b-8818-6588d55fa162"), "Rel-15", 50, new Guid("32dc8d41-539d-ee6a-c954-ec9d6069516a"), true, "REL_15", false },
                    { new Guid("e3595916-3bc7-bed6-6dfb-21d99e251e70"), "4顆", 20, new Guid("74548ab9-989a-2738-f991-df2992ac85d6"), true, "FOUR", false },
                    { new Guid("e9073378-2b39-1d0d-90f2-c10875b0e2ad"), "Taipei", 10, new Guid("43e2d3da-185b-0a63-1ff9-81898e4f8ad7"), true, "TAIPEI", false },
                    { new Guid("ecba1836-89e9-2120-1a46-1f110c7a8e25"), "DHCP Client", 20, new Guid("dc6d955d-4ac6-e2fe-367f-c743e6d67bee"), true, "DHCP_CLIENT", false },
                    { new Guid("ed4fe468-dd25-8a07-dd36-cee1f9ae2719"), "CH165", 70, new Guid("ec9e3bc9-cd97-2743-31ee-1158a82532c8"), true, "CH165", false },
                    { new Guid("fee8a876-19ac-165d-c34f-fe57c756bc83"), "STA", 20, new Guid("8f909c8a-5732-fa1a-eb79-9c0ddb994ef1"), true, "STA", false }
                });

            migrationBuilder.InsertData(
                table: "validation_test_item_parameter_definitions",
                columns: new[] { "id", "code", "design_note", "display_label", "display_order", "field_type", "help_text", "is_enabled", "is_required", "parent_parameter_definition_id", "test_item_definition_id", "validation_json", "was_published" },
                values: new object[,]
                {
                    { new Guid("01460d2c-80a8-a4cc-adc9-83732db39280"), "RF_ANTENNA", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "RF Antenna", 30, "SingleSelect", null, true, false, null, new Guid("f3dd3327-98ec-3465-03f0-6c32b9745c91"), null, false },
                    { new Guid("0d20b671-ad3c-4644-29e0-d34eb508710b"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency", 10, "Text", null, true, false, null, new Guid("c4cd2ed5-5ac9-3f66-2039-9bb4520c208c"), null, false },
                    { new Guid("0f6c07ee-97e6-e41d-4092-a3fc382a9b6b"), "RF_ANTENNA", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "RF Antenna", 30, "Text", null, true, false, null, new Guid("fc74b774-6766-fdad-5ced-312a01c5e3cc"), null, false },
                    { new Guid("13b50b5a-c7cf-6056-010c-a499af9d6d8b"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD Time Slot", 20, "SingleSelect", null, true, false, null, new Guid("2d74eba2-9656-cae5-7362-4d211488b093"), null, false },
                    { new Guid("187ef170-3257-66fc-f8ef-b8d67310ea52"), "RF_ANTENNA", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "RF Antenna", 30, "SingleSelect", null, true, false, null, new Guid("e5572012-d04e-d8fd-d9f9-c86690b545c1"), null, false },
                    { new Guid("226ac489-f012-1482-6259-0bac4da57680"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency / Bandwidth", 10, "SingleSelect", null, true, false, null, new Guid("e5572012-d04e-d8fd-d9f9-c86690b545c1"), null, false },
                    { new Guid("22fa94d8-407e-1144-9439-ffa072b68861"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency / Bandwidth", 10, "SingleSelect", null, true, false, null, new Guid("ef79cb97-c253-b392-8e24-3152a941f35e"), null, false },
                    { new Guid("27a41b83-7a04-fe04-29c1-eea0a65b3fee"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD", 20, "Text", null, true, false, null, new Guid("c4cd2ed5-5ac9-3f66-2039-9bb4520c208c"), null, false },
                    { new Guid("4fc341e9-c6b0-6af0-7dad-99eec7b2cb24"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD Time Slot", 20, "SingleSelect", null, true, false, null, new Guid("e5572012-d04e-d8fd-d9f9-c86690b545c1"), null, false },
                    { new Guid("55e86ebd-717b-593d-c136-548c4a3f0290"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency / Bandwidth", 10, "SingleSelect", null, true, false, null, new Guid("42fc12eb-dbb3-e40a-50db-8979445c4d29"), null, false },
                    { new Guid("5c7a2435-aa95-f3a5-c7d0-754e671acee3"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD (Default DDDSU / Specific)", 20, "SingleSelect", null, true, false, null, new Guid("ef79cb97-c253-b392-8e24-3152a941f35e"), null, false },
                    { new Guid("82699245-cde8-e6bd-e07a-4d44dd2c5504"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency / Bandwidth", 10, "SingleSelect", null, true, false, null, new Guid("2d74eba2-9656-cae5-7362-4d211488b093"), null, false },
                    { new Guid("88f23b86-f7d3-7454-af3a-4d30e0c3b237"), "RF_ANTENNA", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "RF Antenna", 30, "Text", null, true, false, null, new Guid("c4cd2ed5-5ac9-3f66-2039-9bb4520c208c"), null, false },
                    { new Guid("b6eeb7cb-8996-1713-d1c4-b32b557aa626"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency", 10, "Text", null, true, false, null, new Guid("a776c89e-edf2-0995-d13c-ee7b0f17037b"), null, false },
                    { new Guid("ccf11f57-9aee-fa32-fa7a-1e03389c473d"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD", 20, "Text", null, true, false, null, new Guid("fc74b774-6766-fdad-5ced-312a01c5e3cc"), null, false },
                    { new Guid("d097cac9-83d1-5a3b-1e99-c54bf80a9bb4"), "RF_ANTENNA", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "RF Antenna", 30, "SingleSelect", null, true, false, null, new Guid("ef79cb97-c253-b392-8e24-3152a941f35e"), null, false },
                    { new Guid("d3aa16e2-cc3f-396a-2a9d-9a9ef2dc36f7"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD Time Slot", 20, "SingleSelect", null, true, false, null, new Guid("42fc12eb-dbb3-e40a-50db-8979445c4d29"), null, false },
                    { new Guid("d4fb2c79-7015-af3d-bd71-27ec3e9018a5"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD", 20, "Text", null, true, false, null, new Guid("a776c89e-edf2-0995-d13c-ee7b0f17037b"), null, false },
                    { new Guid("d740b486-e048-1806-4543-647d88eab4e2"), "TDD", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "TDD (Default DDDSU / Specific)", 20, "SingleSelect", null, true, false, null, new Guid("f3dd3327-98ec-3465-03f0-6c32b9745c91"), null, false },
                    { new Guid("e4b60562-d769-4e2b-67d2-2747aa354c16"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency / Bandwidth", 10, "SingleSelect", null, true, false, null, new Guid("f3dd3327-98ec-3465-03f0-6c32b9745c91"), null, false },
                    { new Guid("eaeb0b43-b538-372c-de0c-acc5d523fb5f"), "FREQUENCY", "Excel 'maximum groups' is retained as unspecified; no limit is inferred.", "Frequency", 10, "Text", null, true, false, null, new Guid("fc74b774-6766-fdad-5ced-312a01c5e3cc"), null, false }
                });

            migrationBuilder.InsertData(
                table: "validation_field_conditions",
                columns: new[] { "id", "action", "expected_value", "operator", "source_field_code", "target_field_definition_id" },
                values: new object[,]
                {
                    { new Guid("3cd5ad4f-755d-bba6-f2f0-1b90014de748"), "Visible", "BAND_5", "Contains", "WIFI_BAND", new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e") },
                    { new Guid("3fbadd1e-e309-a5a7-d574-eff730cc28a4"), "Visible", "BAND_24", "Contains", "WIFI_BAND", new Guid("3d17a3f1-fe6d-e0db-34d7-dd78744d2c45") },
                    { new Guid("88bb5cd8-496e-ddb3-08f2-5503f41bac4f"), "Visible", "BAND_5", "Contains", "WIFI_BAND", new Guid("ad49a370-510d-fce5-cd2d-2663910d34a2") },
                    { new Guid("90fe8b1e-8723-1427-c051-2a465777856e"), "Required", "BAND_24", "Contains", "WIFI_BAND", new Guid("f47e7209-640c-04d5-13ef-4161d63fb028") },
                    { new Guid("9a9b1954-9a6b-80b6-e806-d6b4fb2abe2a"), "Required", "BAND_5", "Contains", "WIFI_BAND", new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e") },
                    { new Guid("a3d98ae9-2e13-52aa-01bd-0287c9af4387"), "Visible", "BAND_24", "Contains", "WIFI_BAND", new Guid("f47e7209-640c-04d5-13ef-4161d63fb028") },
                    { new Guid("a8232d3f-9f49-256d-3981-737ff60c97e8"), "Visible", "BAND_6", "Contains", "WIFI_BAND", new Guid("a8e534fa-2d36-616e-c51b-db71e4281919") },
                    { new Guid("bc35eb20-a7a2-674c-7597-c20ecd7978a6"), "Required", "BAND_24", "Contains", "WIFI_BAND", new Guid("3d17a3f1-fe6d-e0db-34d7-dd78744d2c45") },
                    { new Guid("c4faa471-051a-86dc-33b5-17a0bf4e8542"), "Visible", "BAND_6", "Contains", "WIFI_BAND", new Guid("efe7b427-459d-01a4-12cb-e18695c01842") },
                    { new Guid("dfe33461-9dbd-e948-bef0-c50fa662f268"), "Required", "BAND_5", "Contains", "WIFI_BAND", new Guid("ad49a370-510d-fce5-cd2d-2663910d34a2") },
                    { new Guid("e2b6186d-6a61-082c-a7ee-282915727101"), "Required", "BAND_6", "Contains", "WIFI_BAND", new Guid("a8e534fa-2d36-616e-c51b-db71e4281919") },
                    { new Guid("ec0ba021-b98a-11b7-4c7a-568cda15b1c7"), "Required", "BAND_6", "Contains", "WIFI_BAND", new Guid("efe7b427-459d-01a4-12cb-e18695c01842") }
                });

            migrationBuilder.InsertData(
                table: "validation_field_options",
                columns: new[] { "id", "display_label", "display_order", "field_definition_id", "is_enabled", "value_code", "was_published" },
                values: new object[,]
                {
                    { new Guid("078631f6-4d26-d798-51ba-169d7f0f82a8"), "3x3", 30, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "3X3", false },
                    { new Guid("07bef3bd-2a89-197b-06ff-5c992d82397a"), "320MHz", 20, new Guid("a8e534fa-2d36-616e-c51b-db71e4281919"), true, "320", false },
                    { new Guid("13f041f7-75ee-fdf2-9414-0e8a579f72ec"), "1x1", 10, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "1X1", false },
                    { new Guid("146433d2-0165-298d-00db-36b82cc2ef26"), "4x4", 20, new Guid("30a6e4e6-2ad1-67ba-84aa-f949d4de7f35"), true, "4X4", false },
                    { new Guid("29e469f0-fe25-b0b9-06bb-e713f5c2eb4a"), "4x4", 40, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "4X4", false },
                    { new Guid("3080b206-4d73-b7a4-7e6a-7b8de76eefd3"), "20MHz", 10, new Guid("3d17a3f1-fe6d-e0db-34d7-dd78744d2c45"), true, "20", false },
                    { new Guid("3ada152b-695e-cd37-56b8-ef39941435f7"), "Yes", 10, new Guid("212c0317-5676-adc3-cb73-b86c392b6790"), true, "YES", false },
                    { new Guid("40972c6f-9a83-8b65-df31-17b561cd0e90"), "5Gbps", 30, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "5G", false },
                    { new Guid("41426e71-dc7f-1b59-f77d-9a134d9c18c2"), "No", 20, new Guid("6084e3ae-45f1-7d13-0244-36d33f1eabbd"), true, "NO", false },
                    { new Guid("492ac6d8-6124-d905-236a-3be3ad6790e4"), "10Gbps", 40, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "10G", false },
                    { new Guid("4c43c7ab-bde4-669d-9e18-b3c89d89da30"), "40MHz", 20, new Guid("3d17a3f1-fe6d-e0db-34d7-dd78744d2c45"), true, "40", false },
                    { new Guid("4d4f0699-ad10-4820-6d38-b8fd7f0ba5ca"), "AC", 10, new Guid("70528943-a0e3-6a97-0556-b1bf5ae5a538"), true, "AC", false },
                    { new Guid("5033d8a8-a0d2-a067-c58a-369dfea0b7ac"), "2x2", 10, new Guid("30a6e4e6-2ad1-67ba-84aa-f949d4de7f35"), true, "2X2", false },
                    { new Guid("52b0891d-7c73-19c6-b612-88d9f25037d4"), "160MHz", 20, new Guid("ad49a370-510d-fce5-cd2d-2663910d34a2"), true, "160", false },
                    { new Guid("553ff43b-627e-9178-ddfa-2e5d44560b47"), "3x3", 30, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "3X3", false },
                    { new Guid("569ffd32-53ad-0c77-271d-e040bec79912"), "Yes", 10, new Guid("6084e3ae-45f1-7d13-0244-36d33f1eabbd"), true, "YES", false },
                    { new Guid("60fb3891-ffe0-80a8-011c-a942c9d95319"), "10Gbps", 30, new Guid("9c147294-50f2-086b-0561-f52925e6eb0b"), true, "10G", false },
                    { new Guid("67b2b3cf-bd42-3449-a98f-1b51516ac123"), "4x4", 40, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "4X4", false },
                    { new Guid("6c6b1806-24eb-6421-052a-36c6c1c2612f"), "1x1", 10, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "1X1", false },
                    { new Guid("70838963-3388-64bd-9dd9-aa6e4b5129c2"), "2.5Gbps", 20, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "2_5G", false },
                    { new Guid("7f9873c7-f0cb-a10f-b1d3-232026239e92"), "5x5", 50, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "5X5", false },
                    { new Guid("88a0a19e-5cb1-7192-8821-03a7af6b1e83"), "SFP+ 10Gbps", 60, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "SFP_10G", false },
                    { new Guid("8eccae22-a4d1-c3e0-f2e3-c87de27cc339"), "DC", 20, new Guid("70528943-a0e3-6a97-0556-b1bf5ae5a538"), true, "DC", false },
                    { new Guid("ac919f64-6964-c424-8bf5-99653d67cb73"), "3x3", 30, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "3X3", false },
                    { new Guid("ad0d9aa3-e29f-807c-c799-b1c164e2b377"), "8x8", 60, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "8X8", false },
                    { new Guid("b0bff7f1-fdb7-cea8-d6d8-2464d94b8513"), "2x2", 20, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "2X2", false },
                    { new Guid("bbee5939-0d3f-b4a0-ba9f-baccfeb6b5dd"), "2.5Gbps", 20, new Guid("9c147294-50f2-086b-0561-f52925e6eb0b"), true, "2_5G", false },
                    { new Guid("bca41403-aa2b-f17d-b1be-ab7d00c9f2fd"), "5x5", 50, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "5X5", false },
                    { new Guid("bcdedd85-f329-af18-2fd5-de12fbd46954"), "2x2", 20, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "2X2", false },
                    { new Guid("c071282f-9523-8569-c07c-01e053b16317"), "SFP+ 1Gbps", 50, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "SFP_1G", false },
                    { new Guid("c0b690c9-ad3f-fa18-1472-c99116898749"), "1Gbps", 10, new Guid("9c147294-50f2-086b-0561-f52925e6eb0b"), true, "1G", false },
                    { new Guid("cd7066fe-5109-b9a2-7e05-28d56f7a5985"), "8x8", 60, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "8X8", false },
                    { new Guid("d1037706-5cfc-75a8-a49a-56e6f173c7b7"), "4x4", 40, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "4X4", false },
                    { new Guid("d82cc146-092d-40b3-b65f-9dbc48f446fb"), "1Gbps", 10, new Guid("48a9f008-ee0e-e71c-4059-cf47621813f1"), true, "1G", false },
                    { new Guid("da8a7f22-1e54-b30e-6300-7bee0732c156"), "160MHz", 10, new Guid("a8e534fa-2d36-616e-c51b-db71e4281919"), true, "160", false },
                    { new Guid("ded994d5-1beb-bfd4-a6da-b2ef51c9f720"), "1Gbps", 10, new Guid("9f2a9ac9-ea45-da5b-1aeb-d3a5419f6c18"), true, "1G", false },
                    { new Guid("e28c144b-5186-9d92-db8a-397e1ed84270"), "1x1", 10, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "1X1", false },
                    { new Guid("eb03c559-9675-e9c8-aa06-5f1d8352a842"), "5x5", 50, new Guid("efe7b427-459d-01a4-12cb-e18695c01842"), true, "5X5", false },
                    { new Guid("f0d740bd-58a7-ef54-dda5-bf3eabf3ddd4"), "8x8", 60, new Guid("f47e7209-640c-04d5-13ef-4161d63fb028"), true, "8X8", false },
                    { new Guid("f4bd36f8-ec90-1f62-c0fd-bbb871355ef1"), "10Gbps", 20, new Guid("48a9f008-ee0e-e71c-4059-cf47621813f1"), true, "10G", false },
                    { new Guid("f5d6ccee-dc46-0905-caa0-3c43aae5cbfe"), "2x2", 20, new Guid("dc585703-4680-7f0c-5265-e81dd3b7488e"), true, "2X2", false },
                    { new Guid("f8613819-80fa-8dd3-d8a6-c2845e32dc9c"), "No", 20, new Guid("212c0317-5676-adc3-cb73-b86c392b6790"), true, "NO", false },
                    { new Guid("fe17de00-198c-78df-afbe-79b597c4b24a"), "80MHz", 10, new Guid("ad49a370-510d-fce5-cd2d-2663910d34a2"), true, "80", false }
                });

            migrationBuilder.InsertData(
                table: "validation_test_item_parameter_options",
                columns: new[] { "id", "display_label", "display_order", "is_enabled", "parameter_definition_id", "value_code", "was_published" },
                values: new object[,]
                {
                    { new Guid("03f635cb-6b37-53e7-656e-5b89ea715ab8"), "External omnidirectional Antenna", 30, true, new Guid("187ef170-3257-66fc-f8ef-b8d67310ea52"), "EXTERNAL_OMNI", false },
                    { new Guid("0d383ea2-06d5-2b60-0bd7-adb9c15d7c3e"), "External directional Antenna", 20, true, new Guid("187ef170-3257-66fc-f8ef-b8d67310ea52"), "EXTERNAL_DIRECTIONAL", false },
                    { new Guid("118657f9-b9e8-132a-7a85-899d069edcd4"), "Specific", 20, true, new Guid("5c7a2435-aa95-f3a5-c7d0-754e671acee3"), "SPECIFIC", false },
                    { new Guid("16aa4d57-6742-b836-03ba-96971d7f4c9a"), "Default", 10, true, new Guid("d740b486-e048-1806-4543-647d88eab4e2"), "DEFAULT", false },
                    { new Guid("1775b621-af3a-6309-f8d1-a676e750e034"), "Default", 10, true, new Guid("5c7a2435-aa95-f3a5-c7d0-754e671acee3"), "DEFAULT", false },
                    { new Guid("1aba12be-256f-458b-9c55-db88277a24cc"), "Specific", 20, true, new Guid("d3aa16e2-cc3f-396a-2a9d-9a9ef2dc36f7"), "SPECIFIC", false },
                    { new Guid("1d0301f9-2cf0-bf98-1442-7fc2c051d202"), "Default", 10, true, new Guid("226ac489-f012-1482-6259-0bac4da57680"), "DEFAULT", false },
                    { new Guid("1d2136a5-0d3c-968c-aef2-f60adef94ac2"), "Default", 10, true, new Guid("82699245-cde8-e6bd-e07a-4d44dd2c5504"), "DEFAULT", false },
                    { new Guid("2dcc844d-2872-9c91-ba86-944225c6c974"), "External omnidirectional Antenna", 30, true, new Guid("01460d2c-80a8-a4cc-adc9-83732db39280"), "EXTERNAL_OMNI", false },
                    { new Guid("30b4723d-2e83-9cd3-4758-7ab32da4dea3"), "Default", 10, true, new Guid("4fc341e9-c6b0-6af0-7dad-99eec7b2cb24"), "DEFAULT", false },
                    { new Guid("43100cb8-cbd2-0952-0ebc-2a6fe3c5b5bf"), "Default", 10, true, new Guid("d3aa16e2-cc3f-396a-2a9d-9a9ef2dc36f7"), "DEFAULT", false },
                    { new Guid("52456a5e-b791-afdd-26aa-8738586b1d75"), "Specific", 20, true, new Guid("226ac489-f012-1482-6259-0bac4da57680"), "SPECIFIC", false },
                    { new Guid("5c70ce61-cec0-28c7-641c-ebce5fa3e90c"), "External directional Antenna", 20, true, new Guid("01460d2c-80a8-a4cc-adc9-83732db39280"), "EXTERNAL_DIRECTIONAL", false },
                    { new Guid("5d3c89d9-0514-6a82-5583-77647ff44fa9"), "Specific", 20, true, new Guid("13b50b5a-c7cf-6056-010c-a499af9d6d8b"), "SPECIFIC", false },
                    { new Guid("5d92a9be-540f-381d-d4d8-a0e7564e73ea"), "Specific", 20, true, new Guid("55e86ebd-717b-593d-c136-548c4a3f0290"), "SPECIFIC", false },
                    { new Guid("6146c995-0abc-b11e-00bd-5449b825ffb8"), "Built-in", 10, true, new Guid("01460d2c-80a8-a4cc-adc9-83732db39280"), "BUILTIN", false },
                    { new Guid("7b086af0-615b-ece5-93b7-b4c6cd5ef7fc"), "Default", 10, true, new Guid("13b50b5a-c7cf-6056-010c-a499af9d6d8b"), "DEFAULT", false },
                    { new Guid("89e4e049-739d-bff8-a93d-b0d6353cf381"), "Built-in", 10, true, new Guid("d097cac9-83d1-5a3b-1e99-c54bf80a9bb4"), "BUILTIN", false },
                    { new Guid("99a0065b-1875-f52b-dc12-f74e6c8bff8f"), "Built-in", 10, true, new Guid("187ef170-3257-66fc-f8ef-b8d67310ea52"), "BUILTIN", false },
                    { new Guid("9cf951bc-e70d-4bcd-dd53-e9b11f0ba4f3"), "External omnidirectional Antenna", 30, true, new Guid("d097cac9-83d1-5a3b-1e99-c54bf80a9bb4"), "EXTERNAL_OMNI", false },
                    { new Guid("9d0967bf-f5ec-9a65-95dc-9f10c05188bb"), "Specific", 20, true, new Guid("22fa94d8-407e-1144-9439-ffa072b68861"), "SPECIFIC", false },
                    { new Guid("adc60e7d-d759-ea1b-e7e4-23d5b634d1fe"), "External directional Antenna", 20, true, new Guid("d097cac9-83d1-5a3b-1e99-c54bf80a9bb4"), "EXTERNAL_DIRECTIONAL", false },
                    { new Guid("b0a7e32a-8f2d-bac4-61b0-44191a5b1f27"), "Default", 10, true, new Guid("e4b60562-d769-4e2b-67d2-2747aa354c16"), "DEFAULT", false },
                    { new Guid("b8a0df5c-fa9b-7f61-d7ec-f44edbdb479c"), "Specific", 20, true, new Guid("e4b60562-d769-4e2b-67d2-2747aa354c16"), "SPECIFIC", false },
                    { new Guid("db2aa9d4-1472-13ec-2c86-ca6244ffacef"), "Specific", 20, true, new Guid("4fc341e9-c6b0-6af0-7dad-99eec7b2cb24"), "SPECIFIC", false },
                    { new Guid("ec95335b-4f58-b0d1-b811-9bee58dfacfb"), "Specific", 20, true, new Guid("82699245-cde8-e6bd-e07a-4d44dd2c5504"), "SPECIFIC", false },
                    { new Guid("eccf4a46-6312-121d-a717-6293e2bca03f"), "Default", 10, true, new Guid("55e86ebd-717b-593d-c136-548c4a3f0290"), "DEFAULT", false },
                    { new Guid("f45bc2b8-4795-21db-54d7-ae9a92d13ffa"), "Default", 10, true, new Guid("22fa94d8-407e-1144-9439-ffa072b68861"), "DEFAULT", false },
                    { new Guid("fe678e6f-cd0c-66c5-51b6-9ff7660c0f86"), "Specific", 20, true, new Guid("d740b486-e048-1806-4543-647d88eab4e2"), "SPECIFIC", false }
                });

            migrationBuilder.CreateIndex(
                name: "IX_validation_field_conditions_target_field_definition_id_sour~",
                table: "validation_field_conditions",
                columns: new[] { "target_field_definition_id", "source_field_code", "operator", "expected_value", "action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_field_definitions_parent_field_definition_id",
                table: "validation_field_definitions",
                column: "parent_field_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_field_definitions_section_definition_id_code",
                table: "validation_field_definitions",
                columns: new[] { "section_definition_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_field_options_field_definition_id_value_code",
                table: "validation_field_options",
                columns: new[] { "field_definition_id", "value_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_form_template_versions_template_id_version_number",
                table: "validation_form_template_versions",
                columns: new[] { "template_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_form_templates_code",
                table: "validation_form_templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_product_type_section_mappings_section_definition~",
                table: "validation_product_type_section_mappings",
                column: "section_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_product_type_test_category_mappings_test_item_ca~",
                table: "validation_product_type_test_category_mappings",
                column: "test_item_category_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_product_types_code",
                table: "validation_product_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_section_definitions_template_version_id_code",
                table: "validation_section_definitions",
                columns: new[] { "template_version_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_test_item_categories_template_version_id_code",
                table: "validation_test_item_categories",
                columns: new[] { "template_version_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_test_item_definitions_test_item_category_id_code",
                table: "validation_test_item_definitions",
                columns: new[] { "test_item_category_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_test_item_parameter_definitions_parent_parameter~",
                table: "validation_test_item_parameter_definitions",
                column: "parent_parameter_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_test_item_parameter_definitions_test_item_defini~",
                table: "validation_test_item_parameter_definitions",
                columns: new[] { "test_item_definition_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_test_item_parameter_options_parameter_definition~",
                table: "validation_test_item_parameter_options",
                columns: new[] { "parameter_definition_id", "value_code" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO menu_items
                    (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
                     requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
                SELECT '648435d4-cc9d-4d95-87f0-f10bb83fa34b'::uuid, s.id, 'validation_requests',
                       '驗證測試申請單', '/validation-requests', '✓',
                       COALESCE((SELECT MIN(i.sort_order) - 10 FROM menu_items i WHERE i.section_id = s.id), 0),
                       TRUE, FALSE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
                FROM menu_sections s
                WHERE LOWER(s.code) = 'department'
                  AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'validation_requests');

                INSERT INTO menu_items
                    (id, section_id, code, title, route_path, icon, sort_order, is_enabled, admin_only,
                     requires_supervisor_approval, module_code, template_type, use_standard_template, created_at, updated_at)
                SELECT 'eb5b05d3-4d4c-4e1d-816e-183ed3fbf983'::uuid, s.id, 'validation_request_settings',
                       '驗證申請單設定', '/validation-request-settings', '⚙',
                       COALESCE((SELECT MIN(i.sort_order) - 10 FROM menu_items i WHERE i.section_id = s.id), 0),
                       TRUE, TRUE, FALSE, NULL, NULL, FALSE, NOW(), NOW()
                FROM menu_sections s
                WHERE LOWER(s.code) = 'administration'
                  AND NOT EXISTS (SELECT 1 FROM menu_items i WHERE i.code = 'validation_request_settings');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM menu_items WHERE code IN ('validation_requests', 'validation_request_settings');");

            migrationBuilder.DropTable(
                name: "validation_field_conditions");

            migrationBuilder.DropTable(
                name: "validation_field_options");

            migrationBuilder.DropTable(
                name: "validation_product_type_section_mappings");

            migrationBuilder.DropTable(
                name: "validation_product_type_test_category_mappings");

            migrationBuilder.DropTable(
                name: "validation_test_item_parameter_options");

            migrationBuilder.DropTable(
                name: "validation_field_definitions");

            migrationBuilder.DropTable(
                name: "validation_product_types");

            migrationBuilder.DropTable(
                name: "validation_test_item_parameter_definitions");

            migrationBuilder.DropTable(
                name: "validation_section_definitions");

            migrationBuilder.DropTable(
                name: "validation_test_item_definitions");

            migrationBuilder.DropTable(
                name: "validation_test_item_categories");

            migrationBuilder.DropTable(
                name: "validation_form_template_versions");

            migrationBuilder.DropTable(
                name: "validation_form_templates");
        }
    }
}
