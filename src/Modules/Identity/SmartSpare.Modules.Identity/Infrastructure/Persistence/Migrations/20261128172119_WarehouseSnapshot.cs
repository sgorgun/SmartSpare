using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Identity's warehouse copy (ADR 0029, PR 7j1): <c>iam.warehouse_snapshot</c> with <c>iam.apply_warehouses</c> as its
    /// only write path, for hub codes in the user list and the hubs of the user form.
    /// </summary>
    internal partial class WarehouseSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouse_snapshot",
                schema: "iam",
                columns: table => new
                {
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Inventory warehouse id (inventory.warehouse.id); no cross-module FK."),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Warehouse code, e.g. KLP."),
                    name = table.Column<string>(type: "character varying(120)", nullable: false, comment: "Warehouse name."),
                    is_central_hub = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = the central hub."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = archived in Inventory; not offered in the user form."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_snapshot", x => x.warehouse_id);
                },
                comment: "Read-only copy of the Inventory warehouses, fed by WarehouseCatalogChanged. Written only by iam.apply_warehouses.");

            migrationBuilder.SqlResource(typeof(WarehouseSnapshot).Assembly, "identity/0020_warehouse_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_snapshot",
                schema: "iam");
        }
    }
}
