using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>Responsible employees of brands and warehouses (ADR 0026 decision 3), with audit and privileges.</summary>
    internal partial class ResponsibleEmployees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "brand_responsible",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    brand_id = table.Column<short>(type: "smallint", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Employee (iam.employee); no FK across modules."),
                    assignment_role = table.Column<string>(type: "character varying(30)", nullable: false, comment: "ref.assignment_role.code for this record kind."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who assigned it."),
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: true, defaultValue: "brand", comment: "Always the record kind of this table; part of the FK to ref.assignment_role.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_responsible", x => x.id);
                    table.CheckConstraint("ck_brand_responsible_kind", "record_kind = 'brand'");
                    table.ForeignKey(
                        name: "fk_brand_responsible_brand",
                        column: x => x.brand_id,
                        principalSchema: "ref",
                        principalTable: "brand",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_brand_responsible_role",
                        columns: x => new { x.record_kind, x.assignment_role },
                        principalSchema: "ref",
                        principalTable: "assignment_role",
                        principalColumns: new[] { "record_kind", "code" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Responsible employees of a brand with their assignment role (ADR 0026); the employee is an id of the Identity module.");

            migrationBuilder.CreateTable(
                name: "warehouse_responsible",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Employee (iam.employee); no FK across modules."),
                    assignment_role = table.Column<string>(type: "character varying(30)", nullable: false, comment: "ref.assignment_role.code for this record kind."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who assigned it."),
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: true, defaultValue: "warehouse", comment: "Always the record kind of this table; part of the FK to ref.assignment_role.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_responsible", x => x.id);
                    table.CheckConstraint("ck_warehouse_responsible_kind", "record_kind = 'warehouse'");
                    table.ForeignKey(
                        name: "fk_warehouse_responsible_role",
                        columns: x => new { x.record_kind, x.assignment_role },
                        principalSchema: "ref",
                        principalTable: "assignment_role",
                        principalColumns: new[] { "record_kind", "code" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_warehouse_responsible_warehouse",
                        column: x => x.warehouse_id,
                        principalSchema: "inventory",
                        principalTable: "warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Responsible employees of a warehouse with their assignment role (ADR 0026); the employee is an id of the Identity module.");

            migrationBuilder.CreateIndex(
                name: "ix_brand_responsible_employee",
                schema: "catalog",
                table: "brand_responsible",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "uq_brand_responsible",
                schema: "catalog",
                table: "brand_responsible",
                columns: new[] { "brand_id", "employee_id", "assignment_role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warehouse_responsible_employee",
                schema: "inventory",
                table: "warehouse_responsible",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "uq_warehouse_responsible",
                schema: "inventory",
                table: "warehouse_responsible",
                columns: new[] { "warehouse_id", "employee_id", "assignment_role" },
                unique: true);

            migrationBuilder.SqlResource(typeof(ResponsibleEmployees).Assembly, "inventory/0013_responsibles.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "brand_responsible",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "warehouse_responsible",
                schema: "inventory");
        }
    }
}
