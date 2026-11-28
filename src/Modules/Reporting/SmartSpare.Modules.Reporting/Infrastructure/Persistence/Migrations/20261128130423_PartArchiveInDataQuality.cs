using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Parts on the shared catalogue mechanism (PR 7i1), step 2 of 3: <c>mart.v_data_quality</c> reads
    /// <c>catalog.part.is_archived</c>, so Inventory can drop <c>is_active</c>.
    /// </summary>
    internal partial class PartArchiveInDataQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(PartArchiveInDataQuality).Assembly, "reporting/0008_part_archive_in_data_quality.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
