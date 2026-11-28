using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Anonymisation also redacts the employee in Identity's <c>EmployeeDirectoryChanged</c> payloads (ADR 0029 amendment 2,
    /// PR 7j1c). SQL only; the model is unchanged.
    /// </summary>
    internal partial class AnonymiseRedactsEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(AnonymiseRedactsEvents).Assembly, "identity/0021_anonymise_redacts_events.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
