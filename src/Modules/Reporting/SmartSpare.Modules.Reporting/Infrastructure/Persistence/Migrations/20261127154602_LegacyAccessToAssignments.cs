using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Migration step 2 of STAGE_7_PLAN 1.6 (ADR 0026 decision 6): the one-time, idempotent copy of legacy brand lists and home
    /// hubs into the responsibility assignments and Identity's projection; the only cross-module data step of the stage.
    /// </summary>
    internal partial class LegacyAccessToAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(LegacyAccessToAssignments).Assembly, "reporting/0005_legacy_access_copy.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
