using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SmartSpare.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Organisation (ADR 0026, STAGE_7_PLAN 1.6, migration step 1): positions with role templates and employees. Every
    /// existing user becomes an employee whose position's template equals the user's roles
    /// (<c>identity/0007_organisation.sql</c>), then <c>app_user.employee_id</c> becomes required.
    /// </summary>
    internal partial class Organisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "employee_id",
                schema: "iam",
                table: "app_user",
                type: "uuid",
                nullable: true,
                comment: "The employee the account belongs to (iam.employee); never null.")
                .Annotation("Relational:ColumnOrder", 15);

            migrationBuilder.CreateTable(
                name: "position",
                schema: "iam",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(30)", nullable: false, comment: "Business key, e.g. BRAND_MANAGER."),
                    name = table.Column<string>(type: "character varying(120)", nullable: false, comment: "Display name."),
                    description = table.Column<string>(type: "character varying(500)", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Archived instead of deleted; cannot be chosen for employees."),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who archived the position (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position", x => x.id);
                    table.CheckConstraint("ck_position_archived", "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
                    table.CheckConstraint("ck_position_code", "((code)::text ~ '^[A-Z0-9_]{2,30}$'::text)");
                },
                comment: "Job positions of the dealership; the role template of a position gives its employees their application roles (ADR 0026).");

            migrationBuilder.CreateTable(
                name: "employee",
                schema: "iam",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Employee number, e.g. E0001."),
                    full_name = table.Column<string>(type: "character varying(150)", nullable: false, comment: "PERSONAL DATA (GDPR). Masked by util.mask_name for read-only roles."),
                    email = table.Column<string>(type: "citext", nullable: true, comment: "PERSONAL DATA (GDPR). Corporate e-mail."),
                    phone = table.Column<string>(type: "character varying(30)", nullable: true, comment: "PERSONAL DATA (GDPR)."),
                    position_id = table.Column<short>(type: "smallint", nullable: false, comment: "The position; its role template gives the account's roles."),
                    hired_on = table.Column<DateOnly>(type: "date", nullable: true),
                    left_on = table.Column<DateOnly>(type: "date", nullable: true, comment: "Set when the employee is archived (left the company)."),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "TRUE = left the company; the account cannot sign in."),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<Guid>(type: "uuid", nullable: true),
                    anonymised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Set by iam.anonymise_employee() (GDPR Art. 17)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee", x => x.id);
                    table.CheckConstraint("ck_employee_anonymised_archived", "((anonymised_at IS NULL) OR is_archived)");
                    table.CheckConstraint("ck_employee_archived", "((is_archived AND (archived_at IS NOT NULL) AND (left_on IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
                    table.CheckConstraint("ck_employee_code", "((code)::text ~ '^E[0-9]{4,6}$'::text)");
                    table.CheckConstraint("ck_employee_dates", "((left_on IS NULL) OR (hired_on IS NULL) OR (left_on >= hired_on))");
                    table.CheckConstraint("ck_employee_email", "((email IS NULL) OR (email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext))");
                    table.CheckConstraint("ck_employee_phone", "((phone IS NULL) OR ((phone)::text ~ '^\\+?[0-9 ()-]{7,20}$'::text))");
                    table.ForeignKey(
                        name: "fk_employee_position",
                        column: x => x.position_id,
                        principalSchema: "iam",
                        principalTable: "position",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Employees of the dealership: the single source of who is who (ADR 0026). Contains personal data (full_name, email, phone).");

            migrationBuilder.CreateTable(
                name: "position_role",
                schema: "iam",
                columns: table => new
                {
                    position_id = table.Column<short>(type: "smallint", nullable: false),
                    role_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to iam.role.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_role", x => new { x.position_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_position_role_position",
                        column: x => x.position_id,
                        principalSchema: "iam",
                        principalTable: "position",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_position_role_role",
                        column: x => x.role_id,
                        principalSchema: "iam",
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Role template: the application roles every employee of the position has.");

            migrationBuilder.CreateIndex(
                name: "uq_app_user_employee",
                schema: "iam",
                table: "app_user",
                column: "employee_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employee_full_name_trgm",
                schema: "iam",
                table: "employee",
                column: "full_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_employee_position",
                schema: "iam",
                table: "employee",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "uq_employee_code",
                schema: "iam",
                table: "employee",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_employee_email",
                schema: "iam",
                table: "employee",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_position_name_trgm",
                schema: "iam",
                table: "position",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "uq_position_code",
                schema: "iam",
                table: "position",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_position_role_role",
                schema: "iam",
                table: "position_role",
                column: "role_id");

            migrationBuilder.AddForeignKey(
                name: "fk_app_user_employee",
                schema: "iam",
                table: "app_user",
                column: "employee_id",
                principalSchema: "iam",
                principalTable: "employee",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Standard positions, existing users -> employees, triggers, grants, views, anonymise_employee.
            migrationBuilder.SqlResource(typeof(Organisation).Assembly, "identity/0007_organisation.sql");

            migrationBuilder.AlterColumn<Guid>(
                name: "employee_id",
                schema: "iam",
                table: "app_user",
                type: "uuid",
                nullable: false,
                comment: "The employee the account belongs to (iam.employee); never null.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldComment: "The employee the account belongs to (iam.employee); never null.")
                .Annotation("Relational:ColumnOrder", 15)
                .OldAnnotation("Relational:ColumnOrder", 15);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_app_user_employee",
                schema: "iam",
                table: "app_user");

            migrationBuilder.DropTable(
                name: "employee",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "position_role",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "position",
                schema: "iam");

            migrationBuilder.DropIndex(
                name: "uq_app_user_employee",
                schema: "iam",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "employee_id",
                schema: "iam",
                table: "app_user");
        }
    }
}
