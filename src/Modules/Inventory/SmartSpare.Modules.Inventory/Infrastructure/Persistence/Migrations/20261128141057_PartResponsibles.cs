using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Responsible employees of parts (STAGE_7_PLAN 3.7, PR 7i1b): <c>catalog.part_responsible</c>, audited, permission-checked
    /// in the database, and a reason of the part archive guard.
    /// </summary>
    internal partial class PartResponsibles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "part_responsible",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Employee (iam.employee); no FK across modules."),
                    assignment_role = table.Column<string>(type: "character varying(30)", nullable: false, comment: "ref.assignment_role.code for this record kind."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who assigned it."),
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: true, defaultValue: "part", comment: "Always the record kind of this table; part of the FK to ref.assignment_role.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_responsible", x => x.id);
                    table.CheckConstraint("ck_part_responsible_kind", "record_kind = 'part'");
                    table.ForeignKey(
                        name: "fk_part_responsible_part",
                        column: x => x.part_id,
                        principalSchema: "catalog",
                        principalTable: "part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_part_responsible_role",
                        columns: x => new { x.record_kind, x.assignment_role },
                        principalSchema: "ref",
                        principalTable: "assignment_role",
                        principalColumns: new[] { "record_kind", "code" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Responsible employees of a part with their assignment role (ADR 0026); the employee is an id of the Identity module.");

            migrationBuilder.CreateIndex(
                name: "ix_part_responsible_employee",
                schema: "catalog",
                table: "part_responsible",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "uq_part_responsible",
                schema: "catalog",
                table: "part_responsible",
                columns: new[] { "part_id", "employee_id", "assignment_role" },
                unique: true);

            migrationBuilder.SqlResource(typeof(PartResponsibles).Assembly, "inventory/0022_part_responsibles.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "part_responsible",
                schema: "catalog");
        }
    }
}
