using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #22: the Data Protection key ring of the single host in <c>iam.data_protection_keys</c>, with its grants
    /// (db/sql/identity/0004_data_protection_keys.sql): app_write only, no audit trigger.
    /// </summary>
    internal partial class AddDataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "iam",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true, comment: "Key id as written by the key manager (key-<guid>)."),
                    xml = table.Column<string>(type: "text", nullable: true, comment: "Key descriptor XML; encrypted at rest when the host configures key protection.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                },
                comment: "ASP.NET Core Data Protection key ring of the single host (cookies, antiforgery tokens). Secret material: no app_read grant, no audit.");

            migrationBuilder.SqlResource(typeof(AddDataProtectionKeys).Assembly, "identity/0004_data_protection_keys.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "iam");
        }
    }
}
