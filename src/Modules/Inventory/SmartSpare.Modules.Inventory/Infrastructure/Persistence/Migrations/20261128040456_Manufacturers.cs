using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Manufacturers (STAGE_7_PLAN 3.1, PR 7h3): <c>catalog.manufacturer</c>, the part's optional <c>manufacturer_id</c>,
    /// and the optional <c>manufacturer_code</c> of the parts import (raw and staging columns, validation and load).
    /// </summary>
    internal partial class Manufacturers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "manufacturer_code",
                schema: "raw",
                table: "part_catalog_row",
                type: "text",
                nullable: true,
                comment: "Manufacturer code; the column is optional in the file (PR 7h3).")
                .Annotation("Relational:ColumnOrder", 10);

            migrationBuilder.AddColumn<short>(
                name: "manufacturer_id",
                schema: "staging",
                table: "part_catalog",
                type: "smallint",
                nullable: true,
                comment: "Resolved manufacturer, if the row named one (PR 7h3).")
                .Annotation("Relational:ColumnOrder", 11);

            migrationBuilder.AddColumn<short>(
                name: "manufacturer_id",
                schema: "catalog",
                table: "part",
                type: "smallint",
                nullable: true,
                comment: "Maker of the part (catalog.manufacturer), if known.")
                .Annotation("Relational:ColumnOrder", 12);

            migrationBuilder.CreateTable(
                name: "manufacturer",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Business key, e.g. MANN; never changes after creation."),
                    name = table.Column<string>(type: "character varying(200)", nullable: false, comment: "Display name."),
                    country_code = table.Column<string>(type: "character(2)", nullable: true, comment: "ISO 3166-1 alpha-2 country, if known."),
                    website = table.Column<string>(type: "character varying(200)", nullable: true, comment: "Website, if known."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Archived instead of deleted: kept on history, cannot be chosen (SS500)."),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who archived the record (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manufacturer", x => x.id);
                    table.CheckConstraint("ck_manufacturer_archived", "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
                    table.CheckConstraint("ck_manufacturer_code", "((code)::text ~ '^[A-Z0-9_]{2,20}$'::text)");
                    table.CheckConstraint("ck_manufacturer_country", "((country_code IS NULL) OR (country_code ~ '^[A-Z]{2}$'::text))");
                },
                comment: "Makers of parts, e.g. MANN+HUMMEL; a part may name one (ADR 0025).");

            migrationBuilder.CreateIndex(
                name: "ix_part_manufacturer",
                schema: "catalog",
                table: "part",
                column: "manufacturer_id");

            migrationBuilder.CreateIndex(
                name: "ix_manufacturer_code_trgm",
                schema: "catalog",
                table: "manufacturer",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_manufacturer_name_trgm",
                schema: "catalog",
                table: "manufacturer",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "uq_manufacturer_code",
                schema: "catalog",
                table: "manufacturer",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_part_manufacturer",
                schema: "catalog",
                table: "part",
                column: "manufacturer_id",
                principalSchema: "catalog",
                principalTable: "manufacturer",
                principalColumn: "id");

            migrationBuilder.SqlResource(typeof(Manufacturers).Assembly, "inventory/0017_manufacturers.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_part_manufacturer",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropTable(
                name: "manufacturer",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_part_manufacturer",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropColumn(
                name: "manufacturer_code",
                schema: "raw",
                table: "part_catalog_row");

            migrationBuilder.DropColumn(
                name: "manufacturer_id",
                schema: "staging",
                table: "part_catalog");

            migrationBuilder.DropColumn(
                name: "manufacturer_id",
                schema: "catalog",
                table: "part");
        }
    }
}
