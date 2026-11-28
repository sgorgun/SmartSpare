using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Responsible employees of manufacturers (STAGE_7_PLAN 3.7, PR 7h4): <c>catalog.manufacturer_responsible</c>, audited.
    /// </summary>
    internal partial class ManufacturerResponsibles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manufacturer_responsible",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    manufacturer_id = table.Column<short>(type: "smallint", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Employee (iam.employee); no FK across modules."),
                    assignment_role = table.Column<string>(type: "character varying(30)", nullable: false, comment: "ref.assignment_role.code for this record kind."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who assigned it."),
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: true, defaultValue: "manufacturer", comment: "Always the record kind of this table; part of the FK to ref.assignment_role.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manufacturer_responsible", x => x.id);
                    table.CheckConstraint("ck_manufacturer_responsible_kind", "record_kind = 'manufacturer'");
                    table.ForeignKey(
                        name: "fk_manufacturer_responsible_manufacturer",
                        column: x => x.manufacturer_id,
                        principalSchema: "catalog",
                        principalTable: "manufacturer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manufacturer_responsible_role",
                        columns: x => new { x.record_kind, x.assignment_role },
                        principalSchema: "ref",
                        principalTable: "assignment_role",
                        principalColumns: new[] { "record_kind", "code" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Responsible employees of a manufacturer with their assignment role (ADR 0026); the employee is an id of the Identity module.");

            migrationBuilder.CreateIndex(
                name: "ix_manufacturer_responsible_employee",
                schema: "catalog",
                table: "manufacturer_responsible",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "uq_manufacturer_responsible",
                schema: "catalog",
                table: "manufacturer_responsible",
                columns: new[] { "manufacturer_id", "employee_id", "assignment_role" },
                unique: true);

            migrationBuilder.SqlResource(typeof(ManufacturerResponsibles).Assembly, "inventory/0018_manufacturer_responsibles.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manufacturer_responsible",
                schema: "catalog");
        }
    }
}
