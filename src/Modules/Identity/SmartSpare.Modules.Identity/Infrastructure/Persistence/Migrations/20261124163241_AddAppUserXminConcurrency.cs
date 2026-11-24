using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Maps PostgreSQL's <c>xmin</c> system column as the optimistic concurrency token of <c>AppUser</c>.
    /// Schema-free: Npgsql emits no DDL for <c>xmin</c>, so up and down only touch the migrations history
    /// (STAGE_3_PLAN section 10, 3a). Every later aggregate that maps <c>xmin</c> gets the same kind of migration.
    /// </summary>
    internal partial class AddAppUserXminConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "iam",
                table: "app_user",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "iam",
                table: "app_user");
        }
    }
}
