using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Parts on the shared catalogue mechanism (PR 7i1), step 3 of 3: <c>catalog.part.is_active</c> is dropped; the archive
    /// columns of <c>PartsAsCatalogue</c> are its one meaning now (STAGE_7_PLAN 2.1).
    /// </summary>
    internal partial class DropPartIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "catalog",
                table: "part");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "catalog",
                table: "part",
                type: "boolean",
                nullable: false,
                defaultValueSql: "true",
                comment: "FALSE = deactivated, kept for history.")
                .Annotation("Relational:ColumnOrder", 9);

            migrationBuilder.Sql("UPDATE catalog.part SET is_active = NOT is_archived;");
        }
    }
}
