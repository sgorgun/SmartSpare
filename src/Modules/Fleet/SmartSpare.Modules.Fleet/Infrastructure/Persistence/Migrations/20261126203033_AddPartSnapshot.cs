using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fleet's read-only copy of the Inventory part catalogue, fed by PartCatalogChanged (STAGE_6_PLAN section 5.2,
    /// MODULE_MAPPING 2.3, #12), with its privileges (db/sql/fleet/0004_part_snapshot.sql).
    /// </summary>
    internal partial class AddPartSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "part_snapshot",
                schema: "fleet",
                columns: table => new
                {
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Inventory part id (catalog.part.id); no cross-module FK."),
                    brand_code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Brand code (ref.brand.code)."),
                    oem_part_number = table.Column<string>(type: "character varying(40)", nullable: false, comment: "OEM part number."),
                    description = table.Column<string>(type: "character varying(300)", nullable: false, comment: "Description."),
                    unit_of_measure = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Unit of measure code (ref.unit_of_measure.code)."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, comment: "FALSE = deactivated in the catalogue."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_snapshot", x => x.part_id);
                },
                comment: "Read-only copy of the Inventory part catalogue for service kits, fed by PartCatalogChanged (MODULE_MAPPING 2.3). Written only by the event consumer.");

            migrationBuilder.CreateIndex(
                name: "ix_part_snapshot_brand_oem",
                schema: "fleet",
                table: "part_snapshot",
                columns: new[] { "brand_code", "oem_part_number" });

            migrationBuilder.SqlResource(typeof(AddPartSnapshot).Assembly, "fleet/0004_part_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "part_snapshot",
                schema: "fleet");
        }
    }
}
