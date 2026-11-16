using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The last migration of the last context: transitional cross-module objects (MODULE_MAPPING 3.2).
    /// EF creates what it models; the embedded scripts in db/sql/ add what it cannot.
    /// </summary>
    internal partial class TransitionalCrossModuleObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TRANSITIONAL cross-module views and LANGUAGE sql functions (MODULE_MAPPING 3.2), removed stage by stage (#11-#14).
            migrationBuilder.SqlResource(typeof(TransitionalCrossModuleObjects).Assembly, "reporting/9000_transitional_cross_module.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
