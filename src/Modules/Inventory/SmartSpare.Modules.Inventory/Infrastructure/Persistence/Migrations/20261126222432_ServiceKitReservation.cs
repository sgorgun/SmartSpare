using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>inventory.reserve_service_kit</c>: soft reservation of a service kit, partial when stock is short, idempotent
    /// per line, balances locked in (part_id, warehouse_id) order (STAGE_6_PLAN 5.3 and 5.7, #12;
    /// db/sql/inventory/0007_service_kit_reservation.sql). No model change.
    /// </summary>
    internal partial class ServiceKitReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ServiceKitReservation).Assembly, "inventory/0007_service_kit_reservation.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION inventory.reserve_service_kit(smallint, bigint[], bigint[], numeric[], timestamptz);");
        }
    }
}
