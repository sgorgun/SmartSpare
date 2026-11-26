using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Maps PostgreSQL's <c>xmin</c> system column as the optimistic concurrency token of the <c>TransferOrder</c> and
    /// <c>WarehousePart</c> aggregates. Schema-free: Npgsql emits no DDL for <c>xmin</c>, so up and down only touch the
    /// migrations history (STAGE_3_PLAN section 7, STAGE_5_PLAN section 1).
    /// </summary>
    internal partial class AddInventoryAggregateConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "inventory",
                table: "warehouse_part",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "inventory",
                table: "transfer_order",
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
                schema: "inventory",
                table: "warehouse_part");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "inventory",
                table: "transfer_order");
        }
    }
}
