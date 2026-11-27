using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>iam.purge_processed_messages</c>: retention of delivered outbox and handled inbox rows (#30, STAGE_6_PLAN 8;
    /// db/sql/identity/0006_message_retention.sql). No model change.
    /// </summary>
    internal partial class MessageRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(MessageRetention).Assembly, "identity/0006_message_retention.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION iam.purge_processed_messages(timestamptz, timestamptz, integer);");
        }
    }
}
