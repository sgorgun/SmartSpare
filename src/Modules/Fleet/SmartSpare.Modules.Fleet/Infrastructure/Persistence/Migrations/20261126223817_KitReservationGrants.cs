using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Privileges of the consumer of ServiceKitReserved: UPDATE of qty_reserved and status (STAGE_6_PLAN 5.3, #12;
    /// db/sql/fleet/0006_kit_reservation_grants.sql). The draft function became module-local in 0001 (MODULE_MAPPING X6).
    /// </summary>
    internal partial class KitReservationGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(KitReservationGrants).Assembly, "fleet/0006_kit_reservation_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE (qty_reserved) ON fleet.service_order_line FROM app_write; REVOKE UPDATE (status) ON fleet.service_order FROM app_write;");
        }
    }
}
