using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Inventory tables as of V016 (ADR 0011, MODULE_MAPPING section 1). EF creates what it models; the
    /// embedded scripts in db/sql/inventory/ add what it cannot. Runs after the Platform migration.
    /// </summary>
    internal partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.EnsureSchema(
                name: "staging");

            migrationBuilder.EnsureSchema(
                name: "raw");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:inventory.reservation_status", "soft,firm,consumed,released,expired")
                .Annotation("Npgsql:Enum:inventory.transfer_status", "proposed,approved,dispatched,received,cancelled")
                .Annotation("Npgsql:Enum:raw.import_source", "part_catalog,demand_history")
                .Annotation("Npgsql:Enum:raw.import_status", "uploaded,validated,loaded,failed");

            // Partitioned ledger first: inventory.demand_exception references it (V005).
            migrationBuilder.SqlResource(typeof(InitialInventory).Assembly, "inventory/0001_stock_movement.sql");

            migrationBuilder.CreateTable(
                name: "demand_exception",
                schema: "inventory",
                columns: table => new
                {
                    movement_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.stock_movement."),
                    movement_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Reference to inventory.stock_movement."),
                    reason = table.Column<string>(type: "character varying(300)", nullable: false),
                    detection_method = table.Column<string>(type: "character varying(20)", nullable: false, defaultValueSql: "'manual'::character varying"),
                    flagged_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    flagged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the flagged event.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demand_exception", x => new { x.movement_id, x.movement_occurred_at });
                    table.CheckConstraint("ck_demand_exception_method", "detection_method IN ('manual', 'auto_3sigma')");
                    table.CheckConstraint("ck_demand_exception_reason", "(length(btrim((reason)::text)) >= 5)");
                    table.ForeignKey(
                        name: "fk_demand_exception_movement",
                        columns: x => new { x.movement_id, x.movement_occurred_at },
                        principalSchema: "inventory",
                        principalTable: "stock_movement",
                        principalColumns: new[] { "id", "occurred_at" },
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "1:1 annotation of a demand movement as exceptional (US08). Kept separate because the ledger is immutable; excluded from ML training.");

            migrationBuilder.CreateTable(
                name: "import_batch",
                schema: "raw",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<ImportSource>(type: "raw.import_source", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", nullable: false, comment: "SHA-256 of the file content; the same file cannot be imported twice."),
                    status = table.Column<ImportStatus>(type: "raw.import_status", nullable: false, defaultValueSql: "'uploaded'::raw.import_status", comment: "Workflow status (state machine guarded by trigger)."),
                    total_rows = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    valid_rows = table.Column<int>(type: "integer", nullable: true),
                    error_rows = table.Column<int>(type: "integer", nullable: true),
                    loaded_rows = table.Column<int>(type: "integer", nullable: true),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who created the record (iam.app_user)."),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the uploaded event."),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the processed event."),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batch", x => x.id);
                    table.UniqueConstraint("uq_import_batch_file", x => new { x.source, x.file_sha256 });
                    table.CheckConstraint("ck_import_batch_counts", "((total_rows >= 0) AND ((COALESCE(valid_rows, 0) + COALESCE(error_rows, 0)) <= total_rows) AND (COALESCE(loaded_rows, 0) <= COALESCE(valid_rows, total_rows)))");
                    table.CheckConstraint("ck_import_batch_processed", "((status = 'uploaded'::raw.import_status) OR (processed_at IS NOT NULL))");
                    table.CheckConstraint("ck_import_batch_sha", "(file_sha256 ~ '^[0-9a-f]{64}$'::text)");
                },
                comment: "One uploaded CSV file: source, SHA-256 (duplicate-file protection), processing status and row counts (load monitoring).");

            migrationBuilder.CreateTable(
                name: "part",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    brand_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to ref.brand."),
                    oem_part_number = table.Column<string>(type: "character varying(40)", nullable: false, comment: "Manufacturer part number, upper-case, normalised during import."),
                    description = table.Column<string>(type: "character varying(300)", nullable: false, comment: "Description."),
                    category_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to ref.part_category."),
                    uom_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to ref.unit_of_measure."),
                    weight_kg = table.Column<decimal>(type: "numeric(9,3)", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(12,2)", nullable: false, comment: "Standard (valuation) cost per unit, EUR. Money is numeric, never float."),
                    is_condition_based = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "TRUE for parts replaced \"as required\" (air/cab filters, finding F6): demand only from the forecast stream."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part", x => x.id);
                    table.UniqueConstraint("uq_part_brand_oem", x => new { x.brand_id, x.oem_part_number });
                    table.CheckConstraint("ck_part_description", "(length(btrim((description)::text)) >= 3)");
                    table.CheckConstraint("ck_part_oem_number", "((oem_part_number)::text ~ '^[A-Z0-9][A-Z0-9.\\-/]{2,39}$'::text)");
                    table.CheckConstraint("ck_part_unit_cost", "(unit_cost >= (0)::numeric)");
                    table.CheckConstraint("ck_part_weight", "((weight_kg IS NULL) OR (weight_kg >= (0)::numeric))");
                    table.ForeignKey(
                        name: "fk_part_brand",
                        column: x => x.brand_id,
                        principalSchema: "ref",
                        principalTable: "brand",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_part_category",
                        column: x => x.category_id,
                        principalSchema: "ref",
                        principalTable: "part_category",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_part_uom",
                        column: x => x.uom_id,
                        principalSchema: "ref",
                        principalTable: "unit_of_measure",
                        principalColumn: "id");
                },
                comment: "Spare part (SKU) catalogue; natural key = brand + OEM part number.");

            migrationBuilder.CreateTable(
                name: "warehouse",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(10)", nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(120)", nullable: false, comment: "Display name."),
                    city = table.Column<string>(type: "character varying(80)", nullable: false),
                    address = table.Column<string>(type: "character varying(200)", nullable: true),
                    is_central_hub = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Flag: central hub."),
                    latitude = table.Column<decimal>(type: "numeric(8,5)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(8,5)", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse", x => x.id);
                    table.UniqueConstraint("uq_warehouse_code", x => x.code);
                    table.CheckConstraint("ck_warehouse_code", "((code)::text ~ '^[A-Z]{3,10}$'::text)");
                    table.CheckConstraint("ck_warehouse_lat", "((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))");
                    table.CheckConstraint("ck_warehouse_lon", "((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))");
                },
                comment: "Regional branch warehouses (Vilnius central hub, Kaunas, Klaipėda, Šiauliai, Panevėžys, Marijampolė, Utena).");

            migrationBuilder.CreateTable(
                name: "demand_history_row",
                schema: "raw",
                columns: table => new
                {
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to raw.import_batch."),
                    line_no = table.Column<int>(type: "integer", nullable: false, comment: "Line number in the uploaded file."),
                    doc_date = table.Column<string>(type: "text", nullable: true),
                    warehouse_code = table.Column<string>(type: "text", nullable: true),
                    brand_code = table.Column<string>(type: "text", nullable: true),
                    oem_part_number = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<string>(type: "text", nullable: true, comment: "Quantity in the unit of measure of the part."),
                    doc_ref = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demand_history_row", x => new { x.import_batch_id, x.line_no });
                    table.CheckConstraint("ck_demand_history_row_line", "(line_no > 0)");
                    table.ForeignKey(
                        name: "fk_demand_history_row_batch",
                        column: x => x.import_batch_id,
                        principalSchema: "raw",
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Raw layer: historical demand (ERP sales/service issues) CSV rows as text.");

            migrationBuilder.CreateTable(
                name: "import_error",
                schema: "staging",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to raw.import_batch."),
                    line_no = table.Column<int>(type: "integer", nullable: false, comment: "Line number in the uploaded file."),
                    column_name = table.Column<string>(type: "character varying(40)", nullable: false),
                    error_code = table.Column<string>(type: "character varying(30)", nullable: false),
                    message = table.Column<string>(type: "character varying(300)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_error", x => x.id);
                    table.CheckConstraint("ck_import_error_code", "error_code IN ('REQUIRED', 'TYPE', 'RANGE', 'FORMAT', 'UNKNOWN_REF', 'DUPLICATE')");
                    table.ForeignKey(
                        name: "fk_import_error_batch",
                        column: x => x.import_batch_id,
                        principalSchema: "raw",
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Data-quality errors found while validating raw rows (shown to the user after an upload).");

            migrationBuilder.CreateTable(
                name: "part_catalog_row",
                schema: "raw",
                columns: table => new
                {
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to raw.import_batch."),
                    line_no = table.Column<int>(type: "integer", nullable: false, comment: "Line number in the uploaded file."),
                    brand_code = table.Column<string>(type: "text", nullable: true),
                    oem_part_number = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true, comment: "Description."),
                    category_code = table.Column<string>(type: "text", nullable: true),
                    uom_code = table.Column<string>(type: "text", nullable: true),
                    weight_kg = table.Column<string>(type: "text", nullable: true),
                    unit_cost = table.Column<string>(type: "text", nullable: true),
                    condition_based = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_catalog_row", x => new { x.import_batch_id, x.line_no });
                    table.CheckConstraint("ck_part_catalog_row_line", "(line_no > 0)");
                    table.ForeignKey(
                        name: "fk_part_catalog_row_batch",
                        column: x => x.import_batch_id,
                        principalSchema: "raw",
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Raw layer: part catalogue CSV rows as text (columns: brand, oem_part_number, description, category, uom, weight_kg, unit_cost, condition_based).");

            migrationBuilder.CreateTable(
                name: "part_analogue",
                schema: "catalog",
                columns: table => new
                {
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    analogue_part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    scaling_factor = table.Column<decimal>(type: "numeric(5,3)", nullable: false, defaultValueSql: "1.000", comment: "Multiplier applied to the analogue's demand history (US15)."),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false, defaultValueSql: "CURRENT_DATE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_part_analogue", x => new { x.part_id, x.analogue_part_id });
                    table.CheckConstraint("ck_part_analogue_distinct", "(part_id <> analogue_part_id)");
                    table.CheckConstraint("ck_part_analogue_factor", "((scaling_factor > (0)::numeric) AND (scaling_factor <= (10)::numeric))");
                    table.ForeignKey(
                        name: "fk_part_analogue_analogue",
                        column: x => x.analogue_part_id,
                        principalSchema: "catalog",
                        principalTable: "part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_part_analogue_part",
                        column: x => x.part_id,
                        principalSchema: "catalog",
                        principalTable: "part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "New SKU -> historical analogue/predecessor SKU with a demand scaling factor (US15).");

            migrationBuilder.CreateTable(
                name: "warehouse_part",
                schema: "inventory",
                columns: table => new
                {
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    bin_location = table.Column<string>(type: "character varying(20)", nullable: true),
                    safety_stock_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "0", comment: "Local safety buffer; a donor hub may only give away stock above it (US10)."),
                    max_stock_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    is_stocked = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "Flag: stocked."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_part", x => new { x.warehouse_id, x.part_id });
                    table.CheckConstraint("ck_warehouse_part_bin", "((bin_location IS NULL) OR ((bin_location)::text ~ '^[A-Z0-9-]{2,20}$'::text))");
                    table.CheckConstraint("ck_warehouse_part_max", "((max_stock_qty IS NULL) OR (max_stock_qty >= safety_stock_qty))");
                    table.CheckConstraint("ck_warehouse_part_safety", "(safety_stock_qty >= (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_warehouse_part_part",
                        column: x => x.part_id,
                        principalSchema: "catalog",
                        principalTable: "part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_warehouse_part_warehouse",
                        column: x => x.warehouse_id,
                        principalSchema: "inventory",
                        principalTable: "warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Stocking policy of a part in a warehouse: shelf location, safety stock, maximum.");

            migrationBuilder.CreateTable(
                name: "warehouse_route",
                schema: "inventory",
                columns: table => new
                {
                    from_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    to_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    distance_km = table.Column<decimal>(type: "numeric(6,1)", nullable: false),
                    transit_hours = table.Column<decimal>(type: "numeric(5,1)", nullable: false, comment: "Planned inter-depot transport time incl. consolidation."),
                    transport_cost_eur = table.Column<decimal>(type: "numeric(10,2)", nullable: false, comment: "Cost of one inter-depot shipment (EUR).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse_route", x => new { x.from_warehouse_id, x.to_warehouse_id });
                    table.CheckConstraint("ck_warehouse_route_cost", "(transport_cost_eur >= (0)::numeric)");
                    table.CheckConstraint("ck_warehouse_route_distance", "(distance_km > (0)::numeric)");
                    table.CheckConstraint("ck_warehouse_route_distinct", "(from_warehouse_id <> to_warehouse_id)");
                    table.CheckConstraint("ck_warehouse_route_transit", "((transit_hours > (0)::numeric) AND (transit_hours <= (240)::numeric))");
                    table.ForeignKey(
                        name: "fk_warehouse_route_from",
                        column: x => x.from_warehouse_id,
                        principalSchema: "inventory",
                        principalTable: "warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_warehouse_route_to",
                        column: x => x.to_warehouse_id,
                        principalSchema: "inventory",
                        principalTable: "warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Fixed inter-depot transport matrix (M:N warehouse-warehouse): distance, transit time, cost. Used by the rebalancing engine (US10).");

            migrationBuilder.CreateTable(
                name: "demand_history",
                schema: "staging",
                columns: table => new
                {
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to raw.demand_history_row."),
                    line_no = table.Column<int>(type: "integer", nullable: false, comment: "Reference to raw.demand_history_row."),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the occurred event."),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: true),
                    part_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", nullable: true, comment: "Quantity in the unit of measure of the part."),
                    doc_ref = table.Column<string>(type: "character varying(40)", nullable: true),
                    row_hash = table.Column<byte[]>(type: "bytea", nullable: true, comment: "SHA-256 of the normalised business key (date|hub|part|doc_ref|qty) for deduplication."),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false, comment: "Flag: valid."),
                    is_duplicate = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "TRUE if the row was already loaded by this or an earlier batch.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_demand_history", x => new { x.import_batch_id, x.line_no });
                    table.CheckConstraint("ck_staging_demand_history_valid", "((NOT is_valid) OR ((occurred_at IS NOT NULL) AND (warehouse_id IS NOT NULL) AND (part_id IS NOT NULL) AND (quantity > (0)::numeric) AND (row_hash IS NOT NULL)))");
                    table.ForeignKey(
                        name: "fk_staging_demand_history_raw",
                        columns: x => new { x.import_batch_id, x.line_no },
                        principalSchema: "raw",
                        principalTable: "demand_history_row",
                        principalColumns: new[] { "import_batch_id", "line_no" },
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Staging layer: typed historical demand rows; duplicates (same row_hash as an earlier loaded row) are flagged, not loaded.");

            migrationBuilder.CreateTable(
                name: "part_catalog",
                schema: "staging",
                columns: table => new
                {
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to raw.part_catalog_row."),
                    line_no = table.Column<int>(type: "integer", nullable: false, comment: "Reference to raw.part_catalog_row."),
                    brand_id = table.Column<short>(type: "smallint", nullable: true),
                    oem_part_number = table.Column<string>(type: "character varying(40)", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", nullable: true, comment: "Description."),
                    category_id = table.Column<int>(type: "integer", nullable: true),
                    uom_id = table.Column<short>(type: "smallint", nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(9,3)", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    is_condition_based = table.Column<bool>(type: "boolean", nullable: true, comment: "Flag: condition based."),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false, comment: "Flag: valid.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_part_catalog", x => new { x.import_batch_id, x.line_no });
                    table.CheckConstraint("ck_staging_part_catalog_valid", "((NOT is_valid) OR ((brand_id IS NOT NULL) AND (oem_part_number IS NOT NULL) AND (description IS NOT NULL) AND (category_id IS NOT NULL) AND (uom_id IS NOT NULL) AND (unit_cost IS NOT NULL)))");
                    table.ForeignKey(
                        name: "fk_staging_part_catalog_raw",
                        columns: x => new { x.import_batch_id, x.line_no },
                        principalSchema: "raw",
                        principalTable: "part_catalog_row",
                        principalColumns: new[] { "import_batch_id", "line_no" },
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Staging layer: typed and validated part catalogue rows (valid rows are upserted into catalog.part).");

            migrationBuilder.CreateTable(
                name: "stock_balance",
                schema: "inventory",
                columns: table => new
                {
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    on_hand_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "0", comment: "Physical quantity in the warehouse (SUM of ledger movements); never negative."),
                    reserved_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "0", comment: "Quantity held by active (soft/firm) reservations; never above on_hand_qty."),
                    available_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: true, computedColumnSql: "(on_hand_qty - reserved_qty)", stored: true, comment: "Generated column: on_hand - reserved (never stored inconsistently)."),
                    last_movement_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the latest physical movement (US04 \"last updated\")."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balance", x => new { x.warehouse_id, x.part_id });
                    table.CheckConstraint("ck_stock_balance_on_hand_non_negative", "(on_hand_qty >= (0)::numeric)");
                    table.CheckConstraint("ck_stock_balance_reserved_le_on_hand", "(reserved_qty <= on_hand_qty)");
                    table.CheckConstraint("ck_stock_balance_reserved_non_negative", "(reserved_qty >= (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_stock_balance_warehouse_part",
                        columns: x => new { x.warehouse_id, x.part_id },
                        principalSchema: "inventory",
                        principalTable: "warehouse_part",
                        principalColumns: new[] { "warehouse_id", "part_id" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Current stock per warehouse and part. Controlled redundancy: = SUM of the ledger, kept in sync by triggers for O(1) reads (NFR01).");

            migrationBuilder.CreateTable(
                name: "transfer_order",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Business document number (assigned by trigger)."),
                    from_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse_route."),
                    to_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse_route."),
                    status = table.Column<TransferStatus>(type: "inventory.transfer_status", nullable: false, defaultValueSql: "'proposed'::inventory.transfer_status", comment: "Workflow status (state machine guarded by trigger)."),
                    is_urgent = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Flag: urgent."),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who created the record (iam.app_user)."),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the approved event."),
                    dispatched_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the dispatched event."),
                    received_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the received event."),
                    cancel_reason = table.Column<string>(type: "character varying(300)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_order", x => x.id);
                    table.UniqueConstraint("uq_transfer_order_number", x => x.number);
                    table.CheckConstraint("ck_transfer_order_approved", "((status = ANY (ARRAY['proposed'::inventory.transfer_status, 'cancelled'::inventory.transfer_status])) OR (approved_at IS NOT NULL))");
                    table.CheckConstraint("ck_transfer_order_cancel", "((status = 'cancelled'::inventory.transfer_status) = (cancel_reason IS NOT NULL))");
                    table.CheckConstraint("ck_transfer_order_dispatched", "((status <> ALL (ARRAY['dispatched'::inventory.transfer_status, 'received'::inventory.transfer_status])) OR (dispatched_at IS NOT NULL))");
                    table.CheckConstraint("ck_transfer_order_distinct", "(from_warehouse_id <> to_warehouse_id)");
                    table.CheckConstraint("ck_transfer_order_number", "((number)::text ~ '^TR-\\d{4}-\\d{6}$'::text)");
                    table.CheckConstraint("ck_transfer_order_received", "((status = 'received'::inventory.transfer_status) = (received_at IS NOT NULL))");
                    table.CheckConstraint("ck_transfer_order_timeline", "((dispatched_at IS NULL) OR (dispatched_at >= approved_at))");
                    table.CheckConstraint("ck_transfer_order_timeline2", "((received_at IS NULL) OR (received_at >= dispatched_at))");
                    table.ForeignKey(
                        name: "fk_transfer_order_route",
                        columns: x => new { x.from_warehouse_id, x.to_warehouse_id },
                        principalSchema: "inventory",
                        principalTable: "warehouse_route",
                        principalColumns: new[] { "from_warehouse_id", "to_warehouse_id" });
                },
                comment: "Inter-warehouse transfer order (TR-YYYY-NNNNNN). Status changes only through inventory.approve/dispatch/receive/cancel_transfer().");

            migrationBuilder.CreateTable(
                name: "transfer_order_line",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transfer_order_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.transfer_order."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Quantity in the unit of measure of the part."),
                    proposed_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: true, comment: "Quantity proposed by the system; differs from quantity only with override_reason (KPI-04)."),
                    override_reason = table.Column<string>(type: "character varying(300)", nullable: true, comment: "Mandatory justification of a manual change (UC01 A2).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_order_line", x => x.id);
                    table.UniqueConstraint("uq_transfer_order_line_part", x => new { x.transfer_order_id, x.part_id });
                    table.CheckConstraint("ck_transfer_order_line_override", "((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL))");
                    table.CheckConstraint("ck_transfer_order_line_proposed", "((proposed_qty IS NULL) OR (proposed_qty > (0)::numeric))");
                    table.CheckConstraint("ck_transfer_order_line_qty", "(quantity > (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_transfer_order_line_order",
                        column: x => x.transfer_order_id,
                        principalSchema: "inventory",
                        principalTable: "transfer_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_transfer_order_line_part",
                        column: x => x.part_id,
                        principalSchema: "catalog",
                        principalTable: "part",
                        principalColumn: "id");
                },
                comment: "Parts and quantities of a transfer; proposed_qty keeps the system proposal for KPI-04 (acceptance rate).");

            migrationBuilder.CreateTable(
                name: "stock_reservation",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.stock_balance."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.stock_balance."),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Quantity in the unit of measure of the part."),
                    status = table.Column<ReservationStatus>(type: "inventory.reservation_status", nullable: false, defaultValueSql: "'soft'::inventory.reservation_status", comment: "Workflow status (state machine guarded by trigger)."),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "End of a soft hold (now + 7 days for service kits, UC02)."),
                    service_order_line_id = table.Column<long>(type: "bigint", nullable: true, comment: "Reference to fleet.service_order_line."),
                    transfer_order_line_id = table.Column<long>(type: "bigint", nullable: true, comment: "Reference to inventory.transfer_order_line."),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who created the record (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the closed event.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservation", x => x.id);
                    table.CheckConstraint("ck_stock_reservation_closed", "((status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status])) = (closed_at IS NULL))");
                    table.CheckConstraint("ck_stock_reservation_one_source", "(num_nonnulls(service_order_line_id, transfer_order_line_id) = 1)");
                    table.CheckConstraint("ck_stock_reservation_quantity", "(quantity > (0)::numeric)");
                    table.CheckConstraint("ck_stock_reservation_soft_expires", "((status <> 'soft'::inventory.reservation_status) OR (expires_at IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_stock_reservation_balance",
                        columns: x => new { x.warehouse_id, x.part_id },
                        principalSchema: "inventory",
                        principalTable: "stock_balance",
                        principalColumns: new[] { "warehouse_id", "part_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_reservation_transfer_line",
                        column: x => x.transfer_order_line_id,
                        principalSchema: "inventory",
                        principalTable: "transfer_order_line",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Stock holds. Active (soft/firm) quantities are summed into stock_balance.reserved_qty by trigger.");

            migrationBuilder.CreateIndex(
                name: "ix_demand_exception_flagged_by",
                schema: "inventory",
                table: "demand_exception",
                column: "flagged_by");

            migrationBuilder.CreateIndex(
                name: "ix_staging_demand_history_hash",
                schema: "staging",
                table: "demand_history",
                column: "row_hash",
                filter: "(is_valid AND (NOT is_duplicate))");

            migrationBuilder.CreateIndex(
                name: "ix_import_batch_status",
                schema: "raw",
                table: "import_batch",
                columns: new[] { "status", "uploaded_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_import_batch_user",
                schema: "raw",
                table: "import_batch",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ix_import_error_batch",
                schema: "staging",
                table: "import_error",
                columns: new[] { "import_batch_id", "line_no" });

            migrationBuilder.CreateIndex(
                name: "ix_part_category",
                schema: "catalog",
                table: "part",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_part_description_trgm",
                schema: "catalog",
                table: "part",
                column: "description")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_part_oem_number_trgm",
                schema: "catalog",
                table: "part",
                column: "oem_part_number")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_part_uom",
                schema: "catalog",
                table: "part",
                column: "uom_id");

            migrationBuilder.CreateIndex(
                name: "ix_part_analogue_analogue",
                schema: "catalog",
                table: "part_analogue",
                column: "analogue_part_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balance_part",
                schema: "inventory",
                table: "stock_balance",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_active",
                schema: "inventory",
                table: "stock_reservation",
                columns: new[] { "warehouse_id", "part_id" },
                filter: "(status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status]))");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_created_by",
                schema: "inventory",
                table: "stock_reservation",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_service_line",
                schema: "inventory",
                table: "stock_reservation",
                column: "service_order_line_id",
                filter: "(service_order_line_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_soft_expiry",
                schema: "inventory",
                table: "stock_reservation",
                column: "expires_at",
                filter: "(status = 'soft'::inventory.reservation_status)");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservation_transfer_line",
                schema: "inventory",
                table: "stock_reservation",
                column: "transfer_order_line_id",
                filter: "(transfer_order_line_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_approved_by",
                schema: "inventory",
                table: "transfer_order",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_dispatched_by",
                schema: "inventory",
                table: "transfer_order",
                column: "dispatched_by");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_open",
                schema: "inventory",
                table: "transfer_order",
                columns: new[] { "status", "created_at" },
                filter: "(status = ANY (ARRAY['proposed'::inventory.transfer_status, 'approved'::inventory.transfer_status, 'dispatched'::inventory.transfer_status]))");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_received_by",
                schema: "inventory",
                table: "transfer_order",
                column: "received_by");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_requested_by",
                schema: "inventory",
                table: "transfer_order",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_to_wh",
                schema: "inventory",
                table: "transfer_order",
                column: "to_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_order_line_part",
                schema: "inventory",
                table: "transfer_order_line",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "uq_warehouse_single_central_hub",
                schema: "inventory",
                table: "warehouse",
                column: "is_central_hub",
                unique: true,
                filter: "is_central_hub");

            migrationBuilder.CreateIndex(
                name: "ix_warehouse_part_part",
                schema: "inventory",
                table: "warehouse_part",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_warehouse_route_to",
                schema: "inventory",
                table: "warehouse_route",
                column: "to_warehouse_id");

            // Intra-module FKs of the ledger, now that their target tables exist.
            migrationBuilder.SqlResource(typeof(InitialInventory).Assembly, "inventory/0003_stock_movement_fks.sql");

            // Ledger, reservations, transfers, exceptional demand, import pipeline (V011, V013, V015).
            migrationBuilder.SqlResource(typeof(InitialInventory).Assembly, "inventory/0002_inventory_logic.sql");

            // Seven hubs and the transport matrix (R001).
            migrationBuilder.SqlResource(typeof(InitialInventory).Assembly, "inventory/seed_warehouses.sql");

            // Privileges on catalog, inventory, raw, staging objects (V015).
            migrationBuilder.SqlResource(typeof(InitialInventory).Assembly, "inventory/0004_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "demand_exception",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "demand_history",
                schema: "staging");

            migrationBuilder.DropTable(
                name: "import_error",
                schema: "staging");

            migrationBuilder.DropTable(
                name: "part_analogue",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "part_catalog",
                schema: "staging");

            migrationBuilder.DropTable(
                name: "stock_reservation",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "demand_history_row",
                schema: "raw");

            migrationBuilder.DropTable(
                name: "part_catalog_row",
                schema: "raw");

            migrationBuilder.DropTable(
                name: "stock_balance",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "transfer_order_line",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "import_batch",
                schema: "raw");

            migrationBuilder.DropTable(
                name: "warehouse_part",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "transfer_order",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "part",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "warehouse_route",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "warehouse",
                schema: "inventory");
        }
    }
}
