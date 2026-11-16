using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Transactional outbox and idempotent inbox of the module (ADR 0003, MODULE_MAPPING 5.6).
    /// EF creates what it models; the embedded scripts in db/sql/ add what it cannot.
    /// </summary>
    internal partial class AddOutboxInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "fleet",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Id of the handled event."),
                    consumer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, comment: "Handler that consumed the event."),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the event was handled.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.message_id, x.consumer });
                },
                comment: "Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Event id; receivers deduplicate by it."),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Time the event occurred."),
                    type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, comment: "CLR type of the event (Namespace.Type, Assembly)."),
                    payload = table.Column<string>(type: "jsonb", nullable: false, comment: "Event serialized as a JSON object."),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time the event was delivered to every consumer; NULL = pending."),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0, comment: "Failed delivery attempts so far."),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, comment: "Last delivery error, if any.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.id);
                    table.CheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_message_payload", "jsonb_typeof(payload) = 'object'");
                },
                comment: "Integration events of this module waiting for delivery (transactional outbox, ADR 0003).");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "fleet",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            // Privileges on the outbox and inbox (MODULE_MAPPING 4.3).
            migrationBuilder.SqlResource(typeof(AddOutboxInbox).Assembly, "fleet/0003_outbox_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "fleet");
        }
    }
}
