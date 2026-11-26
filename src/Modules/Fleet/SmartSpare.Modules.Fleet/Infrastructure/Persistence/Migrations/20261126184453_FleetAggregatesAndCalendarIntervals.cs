using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 6 remap of Fleet (STAGE_6_PLAN section 1): PostgreSQL's <c>xmin</c> as the optimistic concurrency token of
    /// the <c>Machine</c> and <c>ServiceOrder</c> aggregates (schema-free: Npgsql emits no DDL for <c>xmin</c>), and the
    /// OEM calendar interval <c>maintenance_plan.interval_months</c> with its CHECK (F3: due by engine hours or calendar,
    /// whichever comes first).
    /// </summary>
    internal partial class FleetAggregatesAndCalendarIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "fleet",
                table: "service_order",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<short>(
                name: "interval_months",
                schema: "fleet",
                table: "maintenance_plan",
                type: "smallint",
                nullable: true,
                comment: "OEM calendar interval in months, if any; the service is due by engine hours or by calendar, whichever comes first.")
                .Annotation("Relational:ColumnOrder", 5);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "fleet",
                table: "machine",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "ck_maintenance_plan_interval_months",
                schema: "fleet",
                table: "maintenance_plan",
                sql: "((interval_months IS NULL) OR ((interval_months >= 1) AND (interval_months <= 60)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_maintenance_plan_interval_months",
                schema: "fleet",
                table: "maintenance_plan");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "fleet",
                table: "service_order");

            migrationBuilder.DropColumn(
                name: "interval_months",
                schema: "fleet",
                table: "maintenance_plan");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "fleet",
                table: "machine");
        }
    }
}
