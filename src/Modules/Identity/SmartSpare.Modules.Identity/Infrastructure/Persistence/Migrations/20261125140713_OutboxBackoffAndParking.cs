using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class OutboxBackoffAndParking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_message_pending",
                schema: "iam",
                table: "outbox_message");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "exhausted_at",
                schema: "iam",
                table: "outbox_message",
                type: "timestamp with time zone",
                nullable: true,
                comment: "Time the event ran out of delivery attempts and was parked for an operator; NULL = still being delivered.");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "iam",
                table: "outbox_message",
                type: "timestamp with time zone",
                nullable: true,
                comment: "Earliest time of the next delivery attempt after a failure (exponential back-off); NULL = now.");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_exhausted",
                schema: "iam",
                table: "outbox_message",
                column: "exhausted_at",
                filter: "exhausted_at IS NOT NULL AND processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "iam",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL AND exhausted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_message_exhausted",
                schema: "iam",
                table: "outbox_message");

            migrationBuilder.DropIndex(
                name: "ix_outbox_message_pending",
                schema: "iam",
                table: "outbox_message");

            migrationBuilder.DropColumn(
                name: "exhausted_at",
                schema: "iam",
                table: "outbox_message");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "iam",
                table: "outbox_message");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "iam",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");
        }
    }
}
