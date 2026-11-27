using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// UPDATE of qty_issued for the consumer of ServiceKitIssued (STAGE_6_PLAN 5.4; db/sql/fleet/0008). The completion
    /// function became module-local in 0001 (MODULE_MAPPING X7). No model change.
    /// </summary>
    internal partial class ServiceKitIssueGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ServiceKitIssueGrants).Assembly, "fleet/0008_kit_issue_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE (qty_issued) ON fleet.service_order_line FROM app_write;");
        }
    }
}
