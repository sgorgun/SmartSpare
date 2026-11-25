using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// mart.v_data_quality also counts integration events parked in an outbox (#31). Runs after the modules'
    /// OutboxBackoffAndParking migrations, because Reporting is the last module of the catalogue.
    /// </summary>
    internal partial class DataQualityParkedOutboxEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(DataQualityParkedOutboxEvents).Assembly, "reporting/0004_data_quality_outbox.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
