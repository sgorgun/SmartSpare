using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fleet's warehouse copy and archived-hub guard (ADR 0029, PR 7j1): <c>fleet.warehouse_snapshot</c> with
    /// <c>fleet.apply_warehouses</c> as its only write path, and the triggers that refuse an archived hub on a new service order
    /// or as a machine's home hub.
    /// </summary>
    internal partial class WarehouseSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouse_snapshot",
                schema: "fleet",
                columns: table => new
                {
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Inventory warehouse id (inventory.warehouse.id); no cross-module FK."),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Warehouse code, e.g. KLP."),
                    name = table.Column<string>(type: "character varying(120)", nullable: false, comment: "Warehouse name."),
                    is_central_hub = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = the central hub."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = archived in Inventory; no new service order or home hub may use it (SS500)."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_snapshot", x => x.warehouse_id);
                },
                comment: "Read-only copy of the Inventory warehouses, fed by WarehouseCatalogChanged. Written only by fleet.apply_warehouses.");

            migrationBuilder.SqlResource(typeof(WarehouseSnapshot).Assembly, "fleet/0012_warehouse_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_snapshot",
                schema: "fleet");
        }
    }
}
