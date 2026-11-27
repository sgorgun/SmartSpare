using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>Permission checks in SQL instead of role codes (ADR 0028, PR 7x2).</summary>
    public partial class RequirePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(RequirePermission).Assembly, "platform/0009_require_permission.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
