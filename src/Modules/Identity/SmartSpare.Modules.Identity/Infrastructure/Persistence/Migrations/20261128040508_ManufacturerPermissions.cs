using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The manufacturer catalogue's permissions (STAGE_7_PLAN 3.1, PR 7h3, ADR 0028): View for every role that sees parts;
    /// Create, Edit and Archive for Administrator, Inventory planner and the Superadministrator.
    /// </summary>
    internal partial class ManufacturerPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ManufacturerPermissions).Assembly, "identity/0017_manufacturer_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
