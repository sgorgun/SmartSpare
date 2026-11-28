using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The parts catalogue's permissions (STAGE_7_PLAN 3.1, PR 7i1, ADR 0028): Inventory.Part.Create, .Edit and .Archive,
    /// held by default by Administrator, Inventory planner and the Superadministrator, granted through
    /// <c>iam.grant_default_permission</c> (#211).
    /// </summary>
    internal partial class PartPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(PartPermissions).Assembly, "identity/0019_part_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
