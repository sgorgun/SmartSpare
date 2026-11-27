using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Migration step 3 of STAGE_7_PLAN 1.6 (ADR 0026): after a drift guard, the legacy access data goes
    /// (<c>identity/0009_drop_legacy_access.sql</c>). The SQL does the whole change, in the order the guard needs; the
    /// account's personal-data columns are emptied, not dropped, because an applied Reporting script reads one (#152).
    /// </summary>
    internal partial class DropLegacyAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(DropLegacyAccess).Assembly, "identity/0009_drop_legacy_access.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The legacy access data cannot be restored; migrations are forward-only (ADR 0020).");
    }
}
