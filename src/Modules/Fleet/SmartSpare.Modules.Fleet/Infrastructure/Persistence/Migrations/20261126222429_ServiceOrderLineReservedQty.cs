using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fleet's copy of the reserved kit quantity, <c>service_order_line.qty_reserved</c> with 0 &lt;= reserved &lt;= required,
    /// and the module-local <c>fleet.v_service_kit_shortage</c> that reads it (STAGE_6_PLAN 5.3, MODULE_MAPPING X9, #12;
    /// db/sql/fleet/0005_service_kit_shortage.sql).
    /// </summary>
    internal partial class ServiceOrderLineReservedQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "qty_reserved",
                schema: "fleet",
                table: "service_order_line",
                type: "numeric(12,3)",
                nullable: false,
                defaultValueSql: "0",
                comment: "Fleet's copy of the quantity Inventory holds for the line (ServiceKitReserved; MODULE_MAPPING 3.5).")
                .Annotation("Relational:ColumnOrder", 5);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_order_line_reserved",
                schema: "fleet",
                table: "service_order_line",
                sql: "((qty_reserved >= (0)::numeric) AND (qty_reserved <= qty_required))");

            migrationBuilder.SqlResource(typeof(ServiceOrderLineReservedQty).Assembly, "fleet/0005_service_kit_shortage.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW fleet.v_service_kit_shortage;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_order_line_reserved",
                schema: "fleet",
                table: "service_order_line");

            migrationBuilder.DropColumn(
                name: "qty_reserved",
                schema: "fleet",
                table: "service_order_line");
        }
    }
}
