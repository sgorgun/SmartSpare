using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A reading is never lost at an archived hub (ADR 0029, PR 7j1b): <c>fleet.log_engine_hours</c> stores the reading and
    /// drafts nothing when the machine's home hub is archived in Fleet's warehouse copy. SQL only; the model is unchanged.
    /// </summary>
    internal partial class ReadingAtArchivedHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ReadingAtArchivedHub).Assembly, "fleet/0013_reading_at_archived_hub.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
