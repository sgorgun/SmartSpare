using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fleet's brand copy (STAGE_7_PLAN 3.2, PR 7h1): the foreign key to the brand table goes (brands are Inventory's
    /// now), <c>fleet.brand_snapshot</c> takes its place with <c>fleet.apply_brands</c> as its only write path, and the
    /// machine card view reads it.
    /// </summary>
    internal partial class BrandSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_machine_model_brand",
                schema: "fleet",
                table: "machine_model");

            migrationBuilder.AlterColumn<string>(
                name: "brand_code",
                schema: "fleet",
                table: "part_snapshot",
                type: "character varying(10)",
                nullable: false,
                comment: "Brand code (catalog.brand.code, copied as text).",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldComment: "Brand code (ref.brand.code).");

            migrationBuilder.AlterColumn<short>(
                name: "brand_id",
                schema: "fleet",
                table: "machine_model",
                type: "smallint",
                nullable: false,
                comment: "Inventory brand id (catalog.brand.id); no cross-module FK, the name comes from fleet.brand_snapshot.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldComment: "Reference to ref.brand.");

            migrationBuilder.CreateTable(
                name: "brand_snapshot",
                schema: "fleet",
                columns: table => new
                {
                    brand_id = table.Column<short>(type: "smallint", nullable: false, comment: "Inventory brand id (catalog.brand.id); no cross-module FK."),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Brand code, e.g. CLAAS."),
                    name = table.Column<string>(type: "character varying(80)", nullable: false, comment: "Brand name."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = archived in the catalogue."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_snapshot", x => x.brand_id);
                },
                comment: "Read-only copy of the Inventory brands, fed by BrandCatalogChanged. Written only by fleet.apply_brands.");

            migrationBuilder.SqlResource(typeof(BrandSnapshot).Assembly, "fleet/0011_brand_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "brand_snapshot",
                schema: "fleet");

            migrationBuilder.AlterColumn<string>(
                name: "brand_code",
                schema: "fleet",
                table: "part_snapshot",
                type: "character varying(10)",
                nullable: false,
                comment: "Brand code (ref.brand.code).",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldComment: "Brand code (catalog.brand.code, copied as text).");

            migrationBuilder.AlterColumn<short>(
                name: "brand_id",
                schema: "fleet",
                table: "machine_model",
                type: "smallint",
                nullable: false,
                comment: "Reference to ref.brand.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldComment: "Inventory brand id (catalog.brand.id); no cross-module FK, the name comes from fleet.brand_snapshot.");

            migrationBuilder.AddForeignKey(
                name: "fk_machine_model_brand",
                schema: "fleet",
                table: "machine_model",
                column: "brand_id",
                principalSchema: "ref",
                principalTable: "brand",
                principalColumn: "id");
        }
    }
}
