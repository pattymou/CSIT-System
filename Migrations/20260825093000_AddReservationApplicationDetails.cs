using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SIT.DepartmentSystem.Web.Data;

#nullable disable

namespace SIT.DepartmentSystem.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260825093000_AddReservationApplicationDetails")]
public partial class AddReservationApplicationDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "applicant_agent_email", table: "reservations", type: "character varying(320)", maxLength: 320, nullable: true);
        migrationBuilder.AddColumn<string>(name: "applicant_agent_extension", table: "reservations", type: "character varying(30)", maxLength: 30, nullable: true);
        migrationBuilder.AddColumn<string>(name: "applicant_agent_name", table: "reservations", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "customer", table: "reservations", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "note", table: "reservations", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "product_model_name", table: "reservations", type: "character varying(300)", maxLength: 300, nullable: true);
        migrationBuilder.AddColumn<string>(name: "project_sub_pu", table: "reservations", type: "character varying(200)", maxLength: 200, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "applicant_agent_email", table: "reservations");
        migrationBuilder.DropColumn(name: "applicant_agent_extension", table: "reservations");
        migrationBuilder.DropColumn(name: "applicant_agent_name", table: "reservations");
        migrationBuilder.DropColumn(name: "customer", table: "reservations");
        migrationBuilder.DropColumn(name: "note", table: "reservations");
        migrationBuilder.DropColumn(name: "product_model_name", table: "reservations");
        migrationBuilder.DropColumn(name: "project_sub_pu", table: "reservations");
    }
}
