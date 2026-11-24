using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// #11, X18: <c>iam.v_app_user_masked</c> returns <c>home_warehouse_id</c> and reads <c>iam</c> only, so it moves from the
    /// transitional Reporting script to Identity (db/sql/identity/0005_masked_user_view.sql). No model change.
    /// </summary>
    internal partial class ModuleLocalMaskedUserView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.SqlResource(typeof(ModuleLocalMaskedUserView).Assembly, "identity/0005_masked_user_view.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Databases are rebuilt from the migrations until stage 10 (MODULE_MAPPING 3.2); dropping the view returns
            // the schema to the state of AddDataProtectionKeys.
            migrationBuilder.Sql("DROP VIEW IF EXISTS iam.v_app_user_masked;");
        }
    }
}
