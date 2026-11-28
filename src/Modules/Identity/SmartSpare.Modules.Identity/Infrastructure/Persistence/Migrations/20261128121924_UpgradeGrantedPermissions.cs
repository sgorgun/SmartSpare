using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Permissions granted by an upgrade to an edited seeded role (#211): <c>iam.role.is_customised</c> and <c>upgrade_pending</c>,
    /// and <c>iam.grant_default_permission</c> for later permission migrations.
    /// </summary>
    internal partial class UpgradeGrantedPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_customised",
                schema: "iam",
                table: "role",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                comment: "A seeded role whose permissions an administrator changed away from the shipped set (#211).")
                .Annotation("Relational:ColumnOrder", 6);

            migrationBuilder.AddColumn<string>(
                name: "upgrade_pending",
                schema: "iam",
                table: "role",
                type: "character varying(20)",
                nullable: true,
                comment: "Version whose migration granted a permission to this customised role, until an administrator confirms it (#211).")
                .Annotation("Relational:ColumnOrder", 7);

            migrationBuilder.SqlResource(typeof(UpgradeGrantedPermissions).Assembly, "identity/0018_upgrade_granted_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION iam.grant_default_permission(text, text, text);");

            migrationBuilder.DropColumn(
                name: "is_customised",
                schema: "iam",
                table: "role");

            migrationBuilder.DropColumn(
                name: "upgrade_pending",
                schema: "iam",
                table: "role");
        }
    }
}
