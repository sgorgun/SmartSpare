using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class OutboxTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "inventory",
                table: "outbox_message",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                comment: "W3C traceparent of the request that raised the event; the publish span links to it. NULL outside a trace.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "inventory",
                table: "outbox_message");
        }
    }
}
