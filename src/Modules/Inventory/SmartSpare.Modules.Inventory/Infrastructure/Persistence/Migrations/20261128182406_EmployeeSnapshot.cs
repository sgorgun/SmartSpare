using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Inventory's employee copy (ADR 0029 amendment 2, PR 7j1c): <c>inventory.employee_snapshot</c> with
    /// <c>inventory.apply_employees</c> as its only write path, and the no-self-granting trigger on the rights-bearing responsibles.
    /// </summary>
    internal partial class EmployeeSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_snapshot",
                schema: "inventory",
                columns: table => new
                {
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Identity employee id (iam.employee.id); no cross-module FK."),
                    code = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Employee number."),
                    display_name = table.Column<string>(type: "character varying(200)", nullable: false, comment: "Display name; 'Anonymised Employee' after a GDPR erasure."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, comment: "FALSE = left or anonymised; no new assignments."),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true, comment: "The employee's account (iam.app_user.id), if any: the actor id of the audit trail."),
                    source_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "OccurredAt of the event that wrote the row; older events are ignored."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee_snapshot", x => x.employee_id);
                },
                comment: "Read-only copy of the Identity employees, fed by EmployeeDirectoryChanged. Written only by inventory.apply_employees. Holds a display name (personal data, anonymised with the employee).");

            migrationBuilder.CreateIndex(
                name: "ix_employee_snapshot_account",
                schema: "inventory",
                table: "employee_snapshot",
                column: "account_id",
                unique: true,
                filter: "account_id IS NOT NULL");

            migrationBuilder.SqlResource(typeof(EmployeeSnapshot).Assembly, "inventory/0026_employee_snapshot.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_snapshot",
                schema: "inventory");
        }
    }
}
