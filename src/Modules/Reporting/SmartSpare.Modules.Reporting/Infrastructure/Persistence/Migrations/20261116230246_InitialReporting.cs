using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Reporting (mart) objects as of V016; runs after every other context (MODULE_MAPPING 1.7, 5.2).
    /// EF creates what it models; the embedded scripts in db/sql/ add what it cannot.
    /// </summary>
    internal partial class InitialReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mart");

            migrationBuilder.CreateTable(
                name: "refresh_log",
                schema: "mart",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    view_name = table.Column<string>(type: "character varying(80)", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Time of the started event."),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Time of the finished event."),
                    row_count = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Workflow status (state machine guarded by trigger)."),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_log", x => x.id);
                    table.CheckConstraint("ck_refresh_log_status", "status IN ('ok', 'failed')");
                    table.CheckConstraint("ck_refresh_log_time", "(finished_at >= started_at)");
                },
                comment: "Monitoring of data mart refreshes (duration, row count, failures).");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_log_view_time",
                schema: "mart",
                table: "refresh_log",
                columns: new[] { "view_name", "started_at" },
                descending: new[] { false, true });

            // Data marts, KPI views, load monitoring and data quality (V014).
            migrationBuilder.SqlResource(typeof(InitialReporting).Assembly, "reporting/0001_marts.sql");

            // Masked audit trail, owned by Reporting (V014, V015; MODULE_MAPPING 1.7).
            migrationBuilder.SqlResource(typeof(InitialReporting).Assembly, "reporting/0002_audit_masked.sql");

            // Privileges on mart objects (V015).
            migrationBuilder.SqlResource(typeof(InitialReporting).Assembly, "reporting/0003_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_log",
                schema: "mart");
        }
    }
}
