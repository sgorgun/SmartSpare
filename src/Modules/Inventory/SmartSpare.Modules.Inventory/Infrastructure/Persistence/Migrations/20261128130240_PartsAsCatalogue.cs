using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Parts on the shared catalogue mechanism (PR 7i1), step 1 of 3: the archive columns, backfilled from <c>is_active</c>,
    /// and the database guards. Reporting's <c>PartArchiveInDataQuality</c> (step 2) and <c>DropPartIsActive</c> (step 3) follow.
    /// </summary>
    internal partial class PartsAsCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                schema: "catalog",
                table: "part",
                type: "timestamp with time zone",
                nullable: true)
                .Annotation("Relational:ColumnOrder", 14);

            migrationBuilder.AddColumn<Guid>(
                name: "archived_by",
                schema: "catalog",
                table: "part",
                type: "uuid",
                nullable: true,
                comment: "User who archived the record (iam.app_user).")
                .Annotation("Relational:ColumnOrder", 15);

            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                schema: "catalog",
                table: "part",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                comment: "Archived instead of deleted: kept on history, cannot be chosen (SS500).")
                .Annotation("Relational:ColumnOrder", 13);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "catalog",
                table: "part",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "ck_part_archived",
                schema: "catalog",
                table: "part",
                sql: "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");

            migrationBuilder.SqlResource(typeof(PartsAsCatalogue).Assembly, "inventory/0020_parts_as_catalogue.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_part_catalogue_guard ON catalog.part; DROP FUNCTION catalog.trg_part_catalogue_guard();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_part_archived",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropColumn(
                name: "archived_by",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropColumn(
                name: "is_archived",
                schema: "catalog",
                table: "part");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "catalog",
                table: "part");
        }
    }
}
