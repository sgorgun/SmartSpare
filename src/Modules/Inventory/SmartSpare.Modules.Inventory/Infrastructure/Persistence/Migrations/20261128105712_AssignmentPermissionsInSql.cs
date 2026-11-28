using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The permission of every responsibles change checked in the database too (ADR 0028): a statement trigger on the
    /// brand, warehouse and manufacturer responsibles requires the endpoint's permission from the session.
    /// </summary>
    internal partial class AssignmentPermissionsInSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(AssignmentPermissionsInSql).Assembly, "inventory/0019_assignment_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER trg_require_permission ON catalog.brand_responsible;
                DROP TRIGGER trg_require_permission ON inventory.warehouse_responsible;
                DROP TRIGGER trg_require_permission ON catalog.manufacturer_responsible;
                DROP FUNCTION inventory.trg_require_assignment_permission();
                """);
        }
    }
}
