using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartSpare.Infrastructure.Persistence.Platform.Migrations
{
    /// <summary>
    /// Platform schemas ref, util and audit as of V016 (ADR 0011, MODULE_MAPPING 1.1). EF creates the
    /// tables it models; the embedded scripts in db/sql/platform/ add what EF cannot model.
    /// </summary>
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.EnsureSchema(
                name: "util");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            // Schema of the partitioned audit trail, which EF does not create (ExcludeFromMigrations).
            migrationBuilder.EnsureSchema(
                name: "audit");

            // Public schema hardening, schema comments and util functions (V001, V010, V011).
            migrationBuilder.SqlResource(typeof(InitialPlatform).Assembly, "platform/0001_util_helpers.sql");

            migrationBuilder.CreateTable(
                name: "brand",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, comment: "Display name."),
                    country_code = table.Column<string>(type: "character(2)", nullable: false, comment: "ISO 3166-1 alpha-2 country of the manufacturer.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand", x => x.id);
                    table.UniqueConstraint("uq_brand_code", x => x.code);
                    table.UniqueConstraint("uq_brand_name", x => x.name);
                    table.CheckConstraint("ck_brand_country", "country_code ~ '^[A-Z]{2}$'");
                },
                comment: "Machinery / OEM brands (Claas, John Deere, Fendt, Massey Ferguson, Lemken, Väderstad ...).");

            migrationBuilder.CreateTable(
                name: "demand_class",
                schema: "ref",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false, comment: "Surrogate primary key."),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, comment: "Display name."),
                    adi_lower = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    adi_upper = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    cv2_lower = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    cv2_upper = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true, comment: "Description.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demand_class", x => x.code);
                    table.CheckConstraint("ck_demand_class_adi", "adi_lower >= 0 AND adi_upper > adi_lower");
                    table.CheckConstraint("ck_demand_class_cv2", "cv2_lower >= 0 AND cv2_upper > cv2_lower");
                },
                comment: "Syntetos-Boylan demand classification by ADI (cut-off 1.32) and CV² (cut-off 0.49); ranges are [lower, upper).");

            migrationBuilder.CreateTable(
                name: "document_counter",
                schema: "util",
                columns: table => new
                {
                    prefix = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    year = table.Column<short>(type: "smallint", nullable: false),
                    last_value = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_counter", x => new { x.prefix, x.year });
                    table.CheckConstraint("document_counter_last_value_check", "last_value >= 0");
                    table.CheckConstraint("document_counter_prefix_check", "prefix ~ '^[A-Z]{2,4}$'");
                    table.CheckConstraint("document_counter_year_check", "year BETWEEN 2000 AND 2100");
                },
                comment: "Last issued business document number per prefix and year (gap-less numbering).");

            migrationBuilder.CreateTable(
                name: "machine_type",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, comment: "Display name.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_type", x => x.id);
                    table.UniqueConstraint("uq_machine_type_code", x => x.code);
                },
                comment: "Machine types: tractor, combine harvester, seeder, sprayer, forage harvester ...");

            migrationBuilder.CreateTable(
                name: "movement_type",
                schema: "ref",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Surrogate primary key."),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, comment: "Display name."),
                    balance_sign = table.Column<short>(type: "smallint", nullable: false, comment: "+1 increases on-hand stock, -1 decreases it, 0 = no physical effect (imported historical demand)."),
                    is_demand = table.Column<bool>(type: "boolean", nullable: false, comment: "TRUE if the movement represents customer/service demand used for forecasting."),
                    description = table.Column<string>(type: "text", nullable: true, comment: "Description.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movement_type", x => x.code);
                    table.CheckConstraint("ck_movement_type_code", "code ~ '^[A-Z_]{3,20}$'");
                    table.CheckConstraint("ck_movement_type_demand_is_issue", "NOT is_demand OR balance_sign IN (-1, 0)");
                    table.CheckConstraint("ck_movement_type_sign", "balance_sign IN (-1, 0, 1)");
                },
                comment: "Types of stock ledger movements and their effect on the on-hand balance.");

            migrationBuilder.CreateTable(
                name: "operating_condition",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, comment: "Display name."),
                    interval_factor = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false, comment: "Multiplier applied to the OEM interval: 1.00 = normal, 0.50 = halved.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operating_condition", x => x.id);
                    table.UniqueConstraint("uq_operating_condition_code", x => x.code);
                    table.CheckConstraint("ck_operating_condition_factor", "interval_factor > 0 AND interval_factor <= 1");
                },
                comment: "Operating conditions that shorten OEM service intervals (finding F5: e.g. non-prescribed oil halves the interval).");

            migrationBuilder.CreateTable(
                name: "part_category",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    parent_id = table.Column<int>(type: "integer", nullable: true, comment: "Parent category; NULL for a root category (recursive 1:N relationship)."),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, comment: "Display name.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_category", x => x.id);
                    table.UniqueConstraint("uq_part_category_code", x => x.code);
                    table.CheckConstraint("ck_part_category_not_self", "parent_id IS DISTINCT FROM id");
                    table.ForeignKey(
                        name: "fk_part_category_parent",
                        column: x => x.parent_id,
                        principalSchema: "ref",
                        principalTable: "part_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Hierarchical (self-referencing) spare part category tree, e.g. FILTERS > OIL_FILTERS.");

            migrationBuilder.CreateTable(
                name: "season_phase",
                schema: "ref",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Surrogate primary key."),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, comment: "Display name.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_season_phase", x => x.code);
                },
                comment: "Agrotechnical season phases (winter repair, sowing, crop care, harvest, autumn tillage).");

            migrationBuilder.CreateTable(
                name: "unit_of_measure",
                schema: "ref",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, comment: "Display name."),
                    is_fractional = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false, comment: "TRUE if quantities may be fractional (litres of oil); FALSE for pieces.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_of_measure", x => x.id);
                    table.UniqueConstraint("uq_unit_of_measure_code", x => x.code);
                    table.CheckConstraint("ck_unit_of_measure_code", "code ~ '^[A-Z0-9]{1,10}$'");
                },
                comment: "Units of measure of spare parts (PCS, L, KG, M, SET).");

            migrationBuilder.CreateTable(
                name: "agro_calendar_week",
                schema: "ref",
                columns: table => new
                {
                    iso_week = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key."),
                    season_phase_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "Reference to ref.season_phase."),
                    seasonal_weight = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false, comment: "Expert seasonal intensity (1.00 = average week); peaks Feb-Mar and Jul-Sep.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agro_calendar_week", x => x.iso_week);
                    table.CheckConstraint("ck_agro_calendar_week_range", "iso_week BETWEEN 1 AND 53");
                    table.CheckConstraint("ck_agro_calendar_week_weight", "seasonal_weight > 0 AND seasonal_weight <= 5");
                    table.ForeignKey(
                        name: "fk_agro_calendar_week_phase",
                        column: x => x.season_phase_code,
                        principalSchema: "ref",
                        principalTable: "season_phase",
                        principalColumn: "code");
                },
                comment: "ISO week -> season phase mapping for Lithuania; ML feature (EP03, finding F8).");

            migrationBuilder.CreateIndex(
                name: "ix_agro_calendar_week_phase",
                schema: "ref",
                table: "agro_calendar_week",
                column: "season_phase_code");

            migrationBuilder.CreateIndex(
                name: "ix_part_category_parent",
                schema: "ref",
                table: "part_category",
                column: "parent_id");

            // Partitioned audit.audit_log, row-audit trigger function, retention (V009).
            migrationBuilder.SqlResource(typeof(InitialPlatform).Assembly, "platform/0002_audit_log.sql");

            // Reference data (R001, ref part).
            migrationBuilder.SqlResource(typeof(InitialPlatform).Assembly, "platform/seed_ref.sql");

            // Privileges on Platform objects and the privilege self-check view (V015, Platform part).
            migrationBuilder.SqlResource(typeof(InitialPlatform).Assembly, "platform/0003_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agro_calendar_week",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "brand",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "demand_class",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "document_counter",
                schema: "util");

            migrationBuilder.DropTable(
                name: "machine_type",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "movement_type",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "operating_condition",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "part_category",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "unit_of_measure",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "season_phase",
                schema: "ref");
        }
    }
}
