using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>service_order.kit_reserved_at</c>: when Inventory answered ServiceOrderDrafted, so the API can tell a pending kit
    /// reservation ("reserving") from one that found no stock (STAGE_6_PLAN 6, PR 6h1).
    /// </summary>
    internal partial class ServiceOrderKitReservedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "kit_reserved_at",
                schema: "fleet",
                table: "service_order",
                type: "timestamp with time zone",
                nullable: true,
                comment: "When Inventory answered ServiceOrderDrafted (ServiceKitReserved); NULL while the kit reservation is pending.")
                .Annotation("Relational:ColumnOrder", 14);

            migrationBuilder.SqlResource(typeof(ServiceOrderKitReservedAt).Assembly, "fleet/0007_kit_reserved_at_grant.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kit_reserved_at",
                schema: "fleet",
                table: "service_order");
        }
    }
}
