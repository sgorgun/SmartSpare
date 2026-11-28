using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Inventory's projection of Fleet's hub references (ADR 0029, PR 7j1): <c>inventory.fleet_hub_reference</c> with
    /// <c>inventory.apply_fleet_hub_references</c> as its only write path, read by the warehouse archive guard of PR 7j2.
    /// </summary>
    internal partial class FleetHubReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fleet_hub_reference",
                schema: "inventory",
                columns: table => new
                {
                    record_kind = table.Column<string>(type: "character varying(20)", nullable: false, comment: "service_order or machine."),
                    record_id = table.Column<long>(type: "bigint", nullable: false, comment: "Fleet record id (fleet.service_order.id or fleet.machine.id); no cross-module FK."),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "The hub the record holds: the order's hub or the machine's home hub."),
                    is_open = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = an open service order (draft, parts reserved, in progress) or an active machine."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fleet_hub_reference", x => new { x.record_kind, x.record_id });
                    table.CheckConstraint("ck_fleet_hub_reference_kind", "record_kind IN ('service_order', 'machine')");
                },
                comment: "Read-only projection of the Fleet records that hold a hub, fed by HubReferencesChanged. Written only by inventory.apply_fleet_hub_references.");

            migrationBuilder.CreateIndex(
                name: "ix_fleet_hub_reference_open",
                schema: "inventory",
                table: "fleet_hub_reference",
                column: "warehouse_id",
                filter: "is_open");

            migrationBuilder.SqlResource(typeof(FleetHubReferences).Assembly, "inventory/0025_fleet_hub_references.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fleet_hub_reference",
                schema: "inventory");
        }
    }
}
