using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #71: audit rows of UPDATEs carry the record id, computed from the full row before it is reduced to the changed
    /// columns (db/sql/platform/0006_audit_record_id.sql).
    /// </summary>
    public partial class AuditRecordIdOfUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(AuditRecordIdOfUpdates).Assembly, "platform/0006_audit_record_id.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Function bodies are replaced in place (CREATE OR REPLACE); until stage 10 databases are rebuilt from the
            // migrations (MODULE_MAPPING 3.2), so there is no down script.
        }
    }
}
