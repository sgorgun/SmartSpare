using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>The permission catalogue and the role bundles, seeded to reproduce the role policies (ADR 0028).</summary>
    internal partial class Permissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission",
                schema: "iam",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(100)", nullable: false, comment: "Permission name, e.g. Inventory.Transfer.Approve."),
                    module = table.Column<string>(type: "character varying(30)", nullable: false, comment: "Module, for grouping in the UI."),
                    description = table.Column<string>(type: "character varying(200)", nullable: false, comment: "What the permission allows.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission", x => x.code);
                    table.CheckConstraint("ck_permission_code", "((code)::text ~ '^[A-Z][A-Za-z]+\\.[A-Z][A-Za-z]+\\.[A-Z][A-Za-z]+$'::text)");
                },
                comment: "The fixed permission catalogue defined in code (<Module>.<Resource>.<Action>, ADR 0028); roles bundle these.");

            migrationBuilder.CreateTable(
                name: "role_permission",
                schema: "iam",
                columns: table => new
                {
                    role_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to iam.role."),
                    permission = table.Column<string>(type: "character varying(100)", nullable: false, comment: "Reference to iam.permission.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permission", x => new { x.role_id, x.permission });
                    table.ForeignKey(
                        name: "fk_role_permission_permission",
                        column: x => x.permission,
                        principalSchema: "iam",
                        principalTable: "permission",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_permission_role",
                        column: x => x.role_id,
                        principalSchema: "iam",
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Role bundles: a role grants exactly these permissions (additive, no deny rules; ADR 0028).");

            migrationBuilder.CreateIndex(
                name: "ix_role_permission_permission",
                schema: "iam",
                table: "role_permission",
                column: "permission");

            migrationBuilder.SqlResource(typeof(Permissions).Assembly, "identity/0011_permissions.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permission",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "permission",
                schema: "iam");
        }
    }
}
