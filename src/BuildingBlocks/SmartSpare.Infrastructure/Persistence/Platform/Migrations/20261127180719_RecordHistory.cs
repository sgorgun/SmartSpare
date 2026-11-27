using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>The allow-list and the function behind the change history of a catalogue record (ADR 0025).</summary>
    public partial class RecordHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(RecordHistory).Assembly, "platform/0008_record_history.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
