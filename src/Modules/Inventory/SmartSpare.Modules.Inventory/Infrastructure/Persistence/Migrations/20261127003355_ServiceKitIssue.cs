using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>inventory.issue_service_kit</c>: issue of a completed service order's kit, the reserved quantities and the rest up to
    /// the stock, idempotent per line (STAGE_6_PLAN 5.4, F7, MODULE_MAPPING X7, #12; db/sql/inventory/0009). No model change.
    /// </summary>
    internal partial class ServiceKitIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ServiceKitIssue).Assembly, "inventory/0009_service_kit_issue.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION inventory.issue_service_kit(smallint, bigint[], bigint[], numeric[]);");
        }
    }
}
