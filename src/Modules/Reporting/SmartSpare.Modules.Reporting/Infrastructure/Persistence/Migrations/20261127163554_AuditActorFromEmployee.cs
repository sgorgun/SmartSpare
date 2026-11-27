using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>The masked audit trail names the actor from the employee (ADR 0026).</summary>
    internal partial class AuditActorFromEmployee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(AuditActorFromEmployee).Assembly, "reporting/0006_audit_actor_from_employee.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
