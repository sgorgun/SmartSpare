using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>Projection of the rights-bearing responsible employees (ADR 0026 decision 3), fed by ResponsibilitiesChanged.</summary>
    internal partial class ResponsibilityProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_responsibility",
                schema: "iam",
                columns: table => new
                {
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: false),
                    record_id = table.Column<short>(type: "smallint", nullable: false, comment: "Brand or warehouse id of the Inventory module; no FK across modules."),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_role = table.Column<string>(type: "character varying(30)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee_responsibility", x => new { x.record_kind, x.record_id, x.employee_id, x.assignment_role });
                    table.CheckConstraint("ck_employee_responsibility_kind", "record_kind IN ('brand', 'warehouse')");
                    table.ForeignKey(
                        name: "fk_employee_responsibility_employee",
                        column: x => x.employee_id,
                        principalSchema: "iam",
                        principalTable: "employee",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Projection of the rights-bearing responsible employees of brands and warehouses (ADR 0026); written only by the ResponsibilitiesChanged consumer.");

            migrationBuilder.CreateTable(
                name: "responsibility_source",
                schema: "iam",
                columns: table => new
                {
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: false),
                    record_id = table.Column<short>(type: "smallint", nullable: false),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_responsibility_source", x => new { x.record_kind, x.record_id });
                },
                comment: "Newest applied ResponsibilitiesChanged per record (order-safe projection).");

            migrationBuilder.CreateIndex(
                name: "ix_employee_responsibility_employee",
                schema: "iam",
                table: "employee_responsibility",
                column: "employee_id");

            migrationBuilder.SqlResource(typeof(ResponsibilityProjection).Assembly, "identity/0008_responsibility_projection.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_responsibility",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "responsibility_source",
                schema: "iam");
        }
    }
}
