using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>Permission checks in SQL instead of role codes (ADR 0028, PR 7x2).</summary>
    internal partial class RequirePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(RequirePermission).Assembly, "inventory/0015_require_permission.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
