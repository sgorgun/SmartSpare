using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #11, X2: dispatch and receive check the home warehouse from the session (<c>app.home_warehouse_id</c>) instead of
    /// reading <c>iam.app_user</c> (db/sql/inventory/0006_session_context.sql).
    /// </summary>
    internal partial class WarehouseCheckFromSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(WarehouseCheckFromSessionContext).Assembly, "inventory/0006_session_context.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Function bodies are replaced in place (CREATE OR REPLACE); until stage 10 databases are rebuilt from the
            // migrations (MODULE_MAPPING 3.2), so there is no down script.
        }
    }
}
