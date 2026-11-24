using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #11, X1: <c>util.require_role</c> checks the session roles (<c>app.user_roles</c>) instead of reading <c>iam</c>, and
    /// <c>util.current_home_warehouse_id</c> exposes <c>app.home_warehouse_id</c> (db/sql/platform/0005_session_context.sql).
    /// </summary>
    public partial class RoleChecksFromSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(RoleChecksFromSessionContext).Assembly, "platform/0005_session_context.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Function bodies are replaced in place (CREATE OR REPLACE); until stage 10 databases are rebuilt from the
            // migrations (MODULE_MAPPING 3.2), so there is no down script.
        }
    }
}
