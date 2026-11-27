using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>inventory.release_service_kit</c>: releases the open holds of cancelled or late-reserved service order lines
    /// (STAGE_6_PLAN 5.5, MODULE_MAPPING X8, #12; db/sql/inventory/0010). No model change.
    /// </summary>
    internal partial class ServiceKitRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ServiceKitRelease).Assembly, "inventory/0010_service_kit_release.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION inventory.release_service_kit(bigint[]);");
        }
    }
}
