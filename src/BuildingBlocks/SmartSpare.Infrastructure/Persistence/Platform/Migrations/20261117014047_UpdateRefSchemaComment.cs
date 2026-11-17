using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>
    /// The comment on schema <c>ref</c> named the SQL seed scripts that were removed in PR 2e; reference
    /// data is seeded by the Platform migrations. Schema comments are not part of the EF model, so the
    /// change is a SQL part (db/sql/platform/0004_ref_schema_comment.sql) and the snapshot is unchanged.
    /// </summary>
    public partial class UpdateRefSchemaComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(UpdateRefSchemaComment).Assembly, "platform/0004_ref_schema_comment.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the comment written by InitialPlatform (db/sql/platform/0001_util_helpers.sql).
            migrationBuilder.Sql(
                "COMMENT ON SCHEMA ref IS 'Core layer: fixed reference (lookup) data loaded by db/seed scripts.';");
        }
    }
}
