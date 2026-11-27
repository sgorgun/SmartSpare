using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>The locked Superadministrator position and role with every permission (ADR 0028).</summary>
    internal partial class Superadministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_locked",
                schema: "iam",
                table: "position",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                comment: "A built-in position that cannot be changed or archived (the Superadministrator, ADR 0028).");

            migrationBuilder.SqlResource(typeof(Superadministrator).Assembly, "identity/0014_superadministrator.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_locked",
                schema: "iam",
                table: "position");
        }
    }
}
