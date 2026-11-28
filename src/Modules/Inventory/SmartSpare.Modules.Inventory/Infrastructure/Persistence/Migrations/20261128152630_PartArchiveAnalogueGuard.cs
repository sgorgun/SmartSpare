using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A part that replaces others or has interchangeable links cannot be archived (ADR 0027, PR 7i3): two more reasons
    /// of the part archive guard in the database, as in the API.
    /// </summary>
    internal partial class PartArchiveAnalogueGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(PartArchiveAnalogueGuard).Assembly, "inventory/0024_part_archive_analogue_guard.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
