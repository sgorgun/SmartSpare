using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Identity tables as of V016 (ADR 0011, MODULE_MAPPING section 1). EF creates what it models; the
    /// embedded scripts in db/sql/identity/ add what it cannot. Runs after the Platform migration.
    /// </summary>
    internal partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "iam");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "app_user",
                schema: "iam",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()", comment: "Surrogate primary key."),
                    user_name = table.Column<string>(type: "citext", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false, comment: "PERSONAL DATA (GDPR). Corporate e-mail; masked by util.mask_email for read-only roles."),
                    full_name = table.Column<string>(type: "character varying(150)", nullable: false, comment: "PERSONAL DATA (GDPR). Masked by util.mask_name for read-only roles."),
                    phone = table.Column<string>(type: "character varying(30)", nullable: true, comment: "PERSONAL DATA (GDPR). Masked by util.mask_phone for read-only roles."),
                    password_hash = table.Column<string>(type: "text", nullable: true, comment: "ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 100k iterations). Plain text is impossible by CHECK."),
                    security_stamp = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()", comment: "Changes on password/role change; invalidates issued JWT refresh tokens."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history."),
                    failed_login_count = table.Column<short>(type: "smallint", nullable: false, defaultValueSql: "0"),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    home_warehouse_id = table.Column<short>(type: "smallint", nullable: true, comment: "Reference to inventory.warehouse."),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the last login event."),
                    anonymised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Set by iam.anonymise_user() (GDPR Art. 17); the row is kept so audit references stay valid."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                    table.UniqueConstraint("uq_app_user_email", x => x.email);
                    table.UniqueConstraint("uq_app_user_user_name", x => x.user_name);
                    table.CheckConstraint("ck_app_user_active_has_password", "((password_hash IS NOT NULL) OR (NOT is_active))");
                    table.CheckConstraint("ck_app_user_anonymised_inactive", "((anonymised_at IS NULL) OR (NOT is_active))");
                    table.CheckConstraint("ck_app_user_email", "(email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext)");
                    table.CheckConstraint("ck_app_user_failed_logins", "((failed_login_count >= 0) AND (failed_login_count <= 100))");
                    table.CheckConstraint("ck_app_user_password_hash_format", "((password_hash IS NULL) OR (password_hash ~ '^AQAAAA[A-Za-z0-9+/]{76,}={0,2}$'::text))");
                    table.CheckConstraint("ck_app_user_phone", "((phone IS NULL) OR ((phone)::text ~ '^\\+?[0-9 ()-]{7,20}$'::text))");
                    table.CheckConstraint("ck_app_user_user_name", "(user_name ~ '^[a-z0-9._-]{3,50}$'::citext)");
                },
                comment: "Dealership employees using SmartSpare. Contains personal data (full_name, email, phone): masked for app_read / app_audit.");

            migrationBuilder.CreateTable(
                name: "role",
                schema: "iam",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(30)", nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(80)", nullable: false, comment: "Display name."),
                    description = table.Column<string>(type: "text", nullable: true, comment: "Description.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role", x => x.id);
                    table.UniqueConstraint("uq_role_code", x => x.code);
                    table.CheckConstraint("ck_role_code", "((code)::text ~ '^[A-Z_]{3,30}$'::text)");
                },
                comment: "Application (RBAC) roles from the BA: BRAND_MANAGER, SERVICE_ENGINEER, WAREHOUSE_KEEPER, INVENTORY_PLANNER, SPONSOR, ADMINISTRATOR.");

            migrationBuilder.CreateTable(
                name: "user_brand",
                schema: "iam",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Reference to iam.app_user."),
                    brand_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to ref.brand.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_brand", x => new { x.user_id, x.brand_id });
                    table.ForeignKey(
                        name: "fk_user_brand_brand",
                        column: x => x.brand_id,
                        principalSchema: "ref",
                        principalTable: "brand",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_brand_user",
                        column: x => x.user_id,
                        principalSchema: "iam",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "M:N brand manager <-> brands they are responsible for (e.g. persona Tomas: Claas and Lemken).");

            migrationBuilder.CreateTable(
                name: "user_role",
                schema: "iam",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Reference to iam.app_user."),
                    role_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to iam.role."),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the granted event."),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_role", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_role_granted_by",
                        column: x => x.granted_by,
                        principalSchema: "iam",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_user_role_role",
                        column: x => x.role_id,
                        principalSchema: "iam",
                        principalTable: "role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_user",
                        column: x => x.user_id,
                        principalSchema: "iam",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "M:N user <-> role assignment.");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_home_warehouse",
                schema: "iam",
                table: "app_user",
                column: "home_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_brand_brand",
                schema: "iam",
                table: "user_brand",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_granted_by",
                schema: "iam",
                table: "user_role",
                column: "granted_by");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_role",
                schema: "iam",
                table: "user_role",
                column: "role_id");

            // Functions, triggers and audit triggers of iam (V004, V015).
            migrationBuilder.SqlResource(typeof(InitialIdentity).Assembly, "identity/0001_identity.sql");

            // Application roles (R001).
            migrationBuilder.SqlResource(typeof(InitialIdentity).Assembly, "identity/seed_roles.sql");

            // Privileges on iam objects (V015).
            migrationBuilder.SqlResource(typeof(InitialIdentity).Assembly, "identity/0002_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_brand",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "user_role",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "app_user",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "role",
                schema: "iam");
        }
    }
}
