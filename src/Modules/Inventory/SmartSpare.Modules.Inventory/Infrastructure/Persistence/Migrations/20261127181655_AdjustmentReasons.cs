using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>The adjustment reasons catalogue and the reason on every new stock adjustment (ADR 0025, STAGE_7_PLAN 3.1).</summary>
    internal partial class AdjustmentReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adjustment_reason",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(30)", nullable: false, comment: "Business key, e.g. DAMAGED; never changes after creation."),
                    name = table.Column<string>(type: "character varying(200)", nullable: false, comment: "Display name."),
                    direction = table.Column<string>(type: "character varying(4)", nullable: false, comment: "IN: surpluses only; OUT: shortages only; BOTH."),
                    requires_note = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "An adjustment with this reason must have a note."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Archived instead of deleted: kept on history, cannot be chosen (SS500)."),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who archived the record (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adjustment_reason", x => x.id);
                    table.CheckConstraint("ck_adjustment_reason_archived", "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
                    table.CheckConstraint("ck_adjustment_reason_code", "((code)::text ~ '^[A-Z0-9_]{2,30}$'::text)");
                    table.CheckConstraint("ck_adjustment_reason_direction", "((direction)::text = ANY (ARRAY['IN'::text, 'OUT'::text, 'BOTH'::text]))");
                },
                comment: "Why stock is corrected; every stock adjustment names an active reason (ADR 0025).");

            migrationBuilder.CreateIndex(
                name: "ix_adjustment_reason_code_trgm",
                schema: "inventory",
                table: "adjustment_reason",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_adjustment_reason_name_trgm",
                schema: "inventory",
                table: "adjustment_reason",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "uq_adjustment_reason_code",
                schema: "inventory",
                table: "adjustment_reason",
                column: "code",
                unique: true);

            migrationBuilder.SqlResource(typeof(AdjustmentReasons).Assembly, "inventory/0014_adjustment_reasons.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adjustment_reason",
                schema: "inventory");
        }
    }
}
