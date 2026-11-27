using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>Hub access as a list for writes (ADR 0026): <c>util.current_warehouse_ids</c> and <c>util.require_hub</c> read <c>app.warehouse_ids</c>.</summary>
    public partial class HubAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(HubAccess).Assembly, "platform/0007_hub_access.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
