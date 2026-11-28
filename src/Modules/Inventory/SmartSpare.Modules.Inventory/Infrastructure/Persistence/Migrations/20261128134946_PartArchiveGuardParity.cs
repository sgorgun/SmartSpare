using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The database refuses archiving a part for every reason the API does (PR 7i1 follow-up): stock, open transfers and
    /// kit holds, as SS501 with the reasons.
    /// </summary>
    internal partial class PartArchiveGuardParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(PartArchiveGuardParity).Assembly, "inventory/0021_part_archive_guard_parity.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
