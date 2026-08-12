using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingApparatusColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Place",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartNo",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManufacturerNumber",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcurementStaff",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Imei",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Os",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "InspectionDate",
                table: "apparatus",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MaintenanceDate",
                table: "apparatus",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostPrice",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YearsUse",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DaysUse",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceUse",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustodianDepartment",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Agent",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Feature",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Spec",
                table: "apparatus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "apparatus",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Brand", table: "apparatus");
            migrationBuilder.DropColumn(name: "Model", table: "apparatus");
            migrationBuilder.DropColumn(name: "Number", table: "apparatus");
            migrationBuilder.DropColumn(name: "Place", table: "apparatus");
            migrationBuilder.DropColumn(name: "PartNo", table: "apparatus");
            migrationBuilder.DropColumn(name: "Manufacturer", table: "apparatus");
            migrationBuilder.DropColumn(name: "ManufacturerNumber", table: "apparatus");
            migrationBuilder.DropColumn(name: "ProcurementStaff", table: "apparatus");
            migrationBuilder.DropColumn(name: "Imei", table: "apparatus");
            migrationBuilder.DropColumn(name: "Os", table: "apparatus");
            migrationBuilder.DropColumn(name: "OsVersion", table: "apparatus");
            migrationBuilder.DropColumn(name: "InspectionDate", table: "apparatus");
            migrationBuilder.DropColumn(name: "MaintenanceDate", table: "apparatus");
            migrationBuilder.DropColumn(name: "CostPrice", table: "apparatus");
            migrationBuilder.DropColumn(name: "YearsUse", table: "apparatus");
            migrationBuilder.DropColumn(name: "DaysUse", table: "apparatus");
            migrationBuilder.DropColumn(name: "PriceUse", table: "apparatus");
            migrationBuilder.DropColumn(name: "CustodianDepartment", table: "apparatus");
            migrationBuilder.DropColumn(name: "Agent", table: "apparatus");
            migrationBuilder.DropColumn(name: "Feature", table: "apparatus");
            migrationBuilder.DropColumn(name: "Spec", table: "apparatus");
            migrationBuilder.DropColumn(name: "Note", table: "apparatus");
        }
    }
}
