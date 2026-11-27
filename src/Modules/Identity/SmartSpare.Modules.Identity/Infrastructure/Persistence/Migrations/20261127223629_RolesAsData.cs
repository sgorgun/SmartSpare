using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>Roles as data: lock and archive flags, the locked Administrator, the supplier permission (ADR 0028). xmin is a system column.</summary>
    internal partial class RolesAsData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                schema: "iam",
                table: "role",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                comment: "Archived roles cannot be put in a role template.")
                .Annotation("Relational:ColumnOrder", 5);

            migrationBuilder.AddColumn<bool>(
                name: "is_locked",
                schema: "iam",
                table: "role",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                comment: "A built-in role that cannot be changed or archived (ADR 0028).")
                .Annotation("Relational:ColumnOrder", 4);


            migrationBuilder.SqlResource(typeof(RolesAsData).Assembly, "identity/0013_roles_as_data.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_archived",
                schema: "iam",
                table: "role");

            migrationBuilder.DropColumn(
                name: "is_locked",
                schema: "iam",
                table: "role");

        }
    }
}
