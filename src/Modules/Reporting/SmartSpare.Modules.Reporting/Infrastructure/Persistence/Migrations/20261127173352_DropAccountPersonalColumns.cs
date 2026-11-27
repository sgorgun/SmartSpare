using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The masked audit trail reads the employee, then the account's emptied personal-data columns are dropped (ADR 0026).
    /// In Reporting because it owns the dependent view and runs last, so a fresh database still migrates in order.
    /// </summary>
    internal partial class DropAccountPersonalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(DropAccountPersonalColumns).Assembly, "reporting/0007_drop_account_personal_columns.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
