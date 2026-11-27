using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>Assignment roles of responsible employees (ADR 0026), seeded.</summary>
    public partial class AssignmentRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assignment_role",
                schema: "ref",
                columns: table => new
                {
                    record_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Kind of record the role applies to."),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, comment: "Business code, e.g. BRAND_MANAGER."),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, comment: "Display name."),
                    grants_scope = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE if the assignment gives a right: brand approval scope or hub access.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_role", x => new { x.record_kind, x.code });
                    table.CheckConstraint("ck_assignment_role_code", "code ~ '^[A-Z_]{3,30}$'");
                    table.CheckConstraint("ck_assignment_role_kind", "record_kind IN ('brand', 'warehouse', 'manufacturer', 'part', 'supplier')");
                },
                comment: "Roles of responsible employees per record kind (ADR 0026); grants_scope marks the roles that give rights.");

            migrationBuilder.SqlResource(typeof(AssignmentRoles).Assembly, "platform/seed_assignment_roles.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignment_role",
                schema: "ref");
        }
    }
}
