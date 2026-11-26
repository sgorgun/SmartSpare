using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Module-local expiry of service-kit holds: <c>inventory.expire_soft_reservations(timestamptz)</c> replaces the
    /// transitional procedure that wrote <c>fleet.service_order</c> (STAGE_6_PLAN 5.5, MODULE_MAPPING X5, #12;
    /// db/sql/inventory/0008_service_kit_expiry.sql). No model change.
    /// </summary>
    internal partial class ServiceKitExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ServiceKitExpiry).Assembly, "inventory/0008_service_kit_expiry.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Until stage 10 databases are rebuilt from the migrations (MODULE_MAPPING 3.2): no down script for the
            // transitional procedure this replaces.
            migrationBuilder.Sql("DROP FUNCTION inventory.expire_soft_reservations(timestamptz);");
        }
    }
}
