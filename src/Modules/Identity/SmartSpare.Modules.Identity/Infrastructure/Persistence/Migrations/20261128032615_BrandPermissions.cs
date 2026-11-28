using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The brand catalogue's permissions (STAGE_7_PLAN 3.1, PR 7h2, ADR 0028): Inventory.Brand.Create, .Edit and .Archive,
    /// held by default by Administrator, Inventory planner and the Superadministrator.
    /// </summary>
    internal partial class BrandPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(BrandPermissions).Assembly, "identity/0016_brand_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
