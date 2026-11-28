using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Identity's brand copy (STAGE_7_PLAN 3.2, PR 7h1; product owner, 28 November 2026): <c>iam.brand_snapshot</c> with
    /// <c>iam.apply_brands</c> as its only write path, for the brand codes of the user list and form.
    /// </summary>
    internal partial class BrandSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "brand_snapshot",
                schema: "iam",
                columns: table => new
                {
                    brand_id = table.Column<short>(type: "smallint", nullable: false, comment: "Inventory brand id (catalog.brand.id); no cross-module FK."),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Brand code, e.g. CLAAS."),
                    name = table.Column<string>(type: "character varying(80)", nullable: false, comment: "Brand name."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE = archived in the catalogue; not offered for new assignments."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_snapshot", x => x.brand_id);
                },
                comment: "Read-only copy of the Inventory brands, fed by BrandCatalogChanged. Written only by iam.apply_brands.");

            migrationBuilder.SqlResource(typeof(BrandSnapshot).Assembly, "identity/0015_brand_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "brand_snapshot",
                schema: "iam");
        }
    }
}
