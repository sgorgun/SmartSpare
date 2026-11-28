using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Brands become Inventory's catalogue (STAGE_7_PLAN 3.2, decision C1, PR 7h1): <c>ref.brand</c> moves to
    /// <c>catalog.brand</c> with its ids and foreign keys and gains the master-data columns, triggers and grants; the
    /// staging validation reads it there.
    /// </summary>
    internal partial class BrandMove : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<short>(
                name: "brand_id",
                schema: "catalog",
                table: "part",
                type: "smallint",
                nullable: false,
                comment: "Reference to catalog.brand.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldComment: "Reference to ref.brand.");

            migrationBuilder.SqlResource(typeof(BrandMove).Assembly, "inventory/0016_brand_move.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<short>(
                name: "brand_id",
                schema: "catalog",
                table: "part",
                type: "smallint",
                nullable: false,
                comment: "Reference to ref.brand.",
                oldClrType: typeof(short),
                oldType: "smallint",
                oldComment: "Reference to catalog.brand.");
        }
    }
}
