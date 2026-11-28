using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>
    /// The Platform gives up <c>ref.brand</c> (STAGE_7_PLAN 3.2, PR 7h1): Inventory's migration BrandMove moves the table to
    /// <c>catalog.brand</c> with its rows, ids and foreign keys, so this migration drops nothing.
    /// </summary>
    public partial class BrandMovedToInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
