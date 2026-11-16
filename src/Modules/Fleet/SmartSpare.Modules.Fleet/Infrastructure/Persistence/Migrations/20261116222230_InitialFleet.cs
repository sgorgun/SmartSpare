using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fleet tables as of V016 (ADR 0011, MODULE_MAPPING section 1). EF creates what it models; the
    /// embedded scripts in db/sql/fleet/ add what it cannot. Runs after the Platform migration.
    /// </summary>
    internal partial class InitialFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fleet");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:fleet.machine_status", "active,inactive,decommissioned")
                .Annotation("Npgsql:Enum:fleet.reading_source", "manual,telematics,service_visit,import")
                .Annotation("Npgsql:Enum:fleet.service_order_status", "draft,parts_reserved,in_progress,completed,cancelled");

            migrationBuilder.CreateTable(
                name: "customer",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_code = table.Column<string>(type: "character varying(12)", nullable: false),
                    erp_ref_hash = table.Column<byte[]>(type: "bytea", nullable: true, comment: "HMAC-SHA256 of the ERP customer id (util.pseudonymise); allows matching re-imports without storing the id."),
                    region_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    farm_size_ha = table.Column<decimal>(type: "numeric(8,1)", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer", x => x.id);
                    table.UniqueConstraint("uq_customer_code", x => x.customer_code);
                    table.CheckConstraint("ck_customer_code", "((customer_code)::text ~ '^CUST-\\d{6}$'::text)");
                    table.CheckConstraint("ck_customer_erp_ref_hash", "((erp_ref_hash IS NULL) OR (length(erp_ref_hash) = 32))");
                    table.CheckConstraint("ck_customer_farm_size", "((farm_size_ha IS NULL) OR (farm_size_ha > (0)::numeric))");
                },
                comment: "Machine owner (farm). Pseudonymised: only a code and an HMAC of the ERP id are stored, no names or contacts.");

            migrationBuilder.CreateTable(
                name: "machine_model",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    brand_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to ref.brand."),
                    machine_type_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to ref.machine_type."),
                    model_name = table.Column<string>(type: "character varying(80)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_model", x => x.id);
                    table.UniqueConstraint("uq_machine_model_brand_name", x => new { x.brand_id, x.model_name });
                    table.ForeignKey(
                        name: "fk_machine_model_brand",
                        column: x => x.brand_id,
                        principalSchema: "ref",
                        principalTable: "brand",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_machine_model_type",
                        column: x => x.machine_type_id,
                        principalSchema: "ref",
                        principalTable: "machine_type",
                        principalColumn: "id");
                },
                comment: "Machine model, e.g. Claas Lexion 8700, John Deere 6R 150.");

            migrationBuilder.CreateTable(
                name: "machine",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vin = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Vehicle / product identification number (unique)."),
                    model_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to fleet.machine_model."),
                    customer_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to fleet.customer."),
                    operating_condition_id = table.Column<short>(type: "smallint", nullable: false, comment: "Operating condition that shortens OEM intervals (F5)."),
                    home_warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Nearest depot where service kits are reserved."),
                    manufacture_year = table.Column<short>(type: "smallint", nullable: false),
                    current_engine_hours = table.Column<decimal>(type: "numeric(8,1)", nullable: false, defaultValueSql: "0", comment: "Latest odometer value; maintained by the reading trigger."),
                    engine_hours_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the engine hours read event."),
                    status = table.Column<MachineStatus>(type: "fleet.machine_status", nullable: false, defaultValueSql: "'active'::fleet.machine_status", comment: "Workflow status (state machine guarded by trigger)."),
                    commissioned_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine", x => x.id);
                    table.UniqueConstraint("uq_machine_vin", x => x.vin);
                    table.CheckConstraint("ck_machine_commissioned", "((commissioned_on IS NULL) OR (EXTRACT(year FROM commissioned_on) >= (manufacture_year)::numeric))");
                    table.CheckConstraint("ck_machine_engine_hours", "((current_engine_hours >= (0)::numeric) AND (current_engine_hours <= (100000)::numeric))");
                    table.CheckConstraint("ck_machine_vin", "((vin)::text ~ '^[A-Z0-9]{8,20}$'::text)");
                    table.CheckConstraint("ck_machine_year", "((manufacture_year >= 1950) AND (manufacture_year <= 2100))");
                    table.ForeignKey(
                        name: "fk_machine_condition",
                        column: x => x.operating_condition_id,
                        principalSchema: "ref",
                        principalTable: "operating_condition",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_machine_customer",
                        column: x => x.customer_id,
                        principalSchema: "fleet",
                        principalTable: "customer",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_machine_model",
                        column: x => x.model_id,
                        principalSchema: "fleet",
                        principalTable: "machine_model",
                        principalColumn: "id");
                },
                comment: "Registered machine (installed base). current_engine_hours is maintained by the engine-hour reading trigger.");

            migrationBuilder.CreateTable(
                name: "maintenance_plan",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    model_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to fleet.machine_model."),
                    interval_hours = table.Column<int>(type: "integer", nullable: false, comment: "OEM interval in engine hours (e.g. 500)."),
                    name = table.Column<string>(type: "character varying(120)", nullable: false, comment: "Display name."),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_plan", x => x.id);
                    table.UniqueConstraint("uq_maintenance_plan_model_interval", x => new { x.model_id, x.interval_hours });
                    table.CheckConstraint("ck_maintenance_plan_interval", "((interval_hours >= 10) AND (interval_hours <= 20000))");
                    table.ForeignKey(
                        name: "fk_maintenance_plan_model",
                        column: x => x.model_id,
                        principalSchema: "fleet",
                        principalTable: "machine_model",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "OEM service interval of a model (e.g. 500 h engine oil service). The effective interval = interval_hours x operating_condition.interval_factor.");

            migrationBuilder.CreateTable(
                name: "engine_hours_reading",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    machine_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to fleet.machine."),
                    reading_hours = table.Column<decimal>(type: "numeric(8,1)", nullable: false, comment: "Odometer value; must not decrease and must be physically plausible."),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Time the reading was taken in the field (may be earlier than created_at for offline entry)."),
                    source = table.Column<ReadingSource>(type: "fleet.reading_source", nullable: false, defaultValueSql: "'manual'::fleet.reading_source"),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engine_hours_reading", x => x.id);
                    table.UniqueConstraint("uq_engine_hours_reading_machine_time", x => new { x.machine_id, x.read_at });
                    table.CheckConstraint("ck_engine_hours_reading_hours", "((reading_hours >= (0)::numeric) AND (reading_hours <= (100000)::numeric))");
                    table.CheckConstraint("ck_engine_hours_reading_not_future", "(read_at <= (created_at + '00:05:00'::interval))");
                    table.ForeignKey(
                        name: "fk_engine_hours_reading_machine",
                        column: x => x.machine_id,
                        principalSchema: "fleet",
                        principalTable: "machine",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Odometer history (US05). Monotonic and physically plausible readings are enforced by trigger.");

            migrationBuilder.CreateTable(
                name: "maintenance_plan_part",
                schema: "fleet",
                columns: table => new
                {
                    maintenance_plan_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to fleet.maintenance_plan."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Quantity of the part in the service kit.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_plan_part", x => new { x.maintenance_plan_id, x.part_id });
                    table.CheckConstraint("ck_maintenance_plan_part_qty", "(quantity > (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_maintenance_plan_part_plan",
                        column: x => x.maintenance_plan_id,
                        principalSchema: "fleet",
                        principalTable: "maintenance_plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Service kit / bill of materials of a maintenance plan (M:N plan <-> part with quantity).");

            migrationBuilder.CreateTable(
                name: "service_order",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Business document number (assigned by trigger)."),
                    machine_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to fleet.machine."),
                    maintenance_plan_id = table.Column<int>(type: "integer", nullable: true, comment: "Reference to fleet.maintenance_plan."),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    status = table.Column<ServiceOrderStatus>(type: "fleet.service_order_status", nullable: false, defaultValueSql: "'draft'::fleet.service_order_status", comment: "Workflow status (state machine guarded by trigger)."),
                    due_engine_hours = table.Column<decimal>(type: "numeric(8,1)", nullable: true, comment: "Engine-hour threshold this planned service covers (e.g. 500, 1000, 1500 ...)."),
                    trigger_engine_hours = table.Column<decimal>(type: "numeric(8,1)", nullable: true, comment: "Engine hours of the reading that triggered the draft."),
                    completed_engine_hours = table.Column<decimal>(type: "numeric(8,1)", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who created the record (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the completed event."),
                    cancel_reason = table.Column<string>(type: "character varying(300)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_order", x => x.id);
                    table.UniqueConstraint("uq_service_order_number", x => x.number);
                    table.CheckConstraint("ck_service_order_cancel", "((status = 'cancelled'::fleet.service_order_status) = (cancel_reason IS NOT NULL))");
                    table.CheckConstraint("ck_service_order_completed", "((status = 'completed'::fleet.service_order_status) = ((completed_at IS NOT NULL) AND (completed_engine_hours IS NOT NULL)))");
                    table.CheckConstraint("ck_service_order_number", "((number)::text ~ '^SO-\\d{4}-\\d{6}$'::text)");
                    table.CheckConstraint("ck_service_order_planned_due", "((maintenance_plan_id IS NULL) OR (due_engine_hours IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_service_order_machine",
                        column: x => x.machine_id,
                        principalSchema: "fleet",
                        principalTable: "machine",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_service_order_plan",
                        column: x => x.maintenance_plan_id,
                        principalSchema: "fleet",
                        principalTable: "maintenance_plan",
                        principalColumn: "id");
                },
                comment: "Service order (SO-YYYY-NNNNNN). Planned (plan + due hours) or unplanned repair (plan NULL). Completed orders form the maintenance history.");

            migrationBuilder.CreateTable(
                name: "service_order_line",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_order_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to fleet.service_order."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    qty_required = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Quantity in the unit of measure of the part."),
                    qty_issued = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "0", comment: "Quantity already issued from stock (ISSUE_SERVICE).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_order_line", x => x.id);
                    table.UniqueConstraint("uq_service_order_line_part", x => new { x.service_order_id, x.part_id });
                    table.CheckConstraint("ck_service_order_line_issued", "((qty_issued >= (0)::numeric) AND (qty_issued <= qty_required))");
                    table.CheckConstraint("ck_service_order_line_required", "(qty_required > (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_service_order_line_order",
                        column: x => x.service_order_id,
                        principalSchema: "fleet",
                        principalTable: "service_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Parts required by a service order (copied from the kit, editable for repairs).");

            migrationBuilder.CreateIndex(
                name: "ix_customer_region_warehouse",
                schema: "fleet",
                table: "customer",
                column: "region_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_engine_hours_reading_user",
                schema: "fleet",
                table: "engine_hours_reading",
                column: "recorded_by");

            migrationBuilder.CreateIndex(
                name: "ix_machine_condition",
                schema: "fleet",
                table: "machine",
                column: "operating_condition_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_customer",
                schema: "fleet",
                table: "machine",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_home_wh_active",
                schema: "fleet",
                table: "machine",
                column: "home_warehouse_id",
                filter: "(status = 'active'::fleet.machine_status)");

            migrationBuilder.CreateIndex(
                name: "ix_machine_model",
                schema: "fleet",
                table: "machine",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_vin_trgm",
                schema: "fleet",
                table: "machine",
                column: "vin")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_machine_model_type",
                schema: "fleet",
                table: "machine_model",
                column: "machine_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plan_part_part",
                schema: "fleet",
                table: "maintenance_plan_part",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_order_created_by",
                schema: "fleet",
                table: "service_order",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_service_order_open",
                schema: "fleet",
                table: "service_order",
                columns: new[] { "warehouse_id", "status" },
                filter: "(status = ANY (ARRAY['draft'::fleet.service_order_status, 'parts_reserved'::fleet.service_order_status, 'in_progress'::fleet.service_order_status]))");

            migrationBuilder.CreateIndex(
                name: "ix_service_order_plan",
                schema: "fleet",
                table: "service_order",
                column: "maintenance_plan_id");

            migrationBuilder.CreateIndex(
                name: "uq_service_order_machine_plan_due",
                schema: "fleet",
                table: "service_order",
                columns: new[] { "machine_id", "maintenance_plan_id", "due_engine_hours" },
                unique: true,
                filter: "((status <> 'cancelled'::fleet.service_order_status) AND (maintenance_plan_id IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "ix_service_order_line_part",
                schema: "fleet",
                table: "service_order_line",
                column: "part_id");

            // Engine hours, maintenance due, service orders (V006, V011, V012, V015).
            migrationBuilder.SqlResource(typeof(InitialFleet).Assembly, "fleet/0001_fleet_logic.sql");

            // Privileges on fleet objects (V015).
            migrationBuilder.SqlResource(typeof(InitialFleet).Assembly, "fleet/0002_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engine_hours_reading",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "maintenance_plan_part",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "service_order_line",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "service_order",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "machine",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "maintenance_plan",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "customer",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "machine_model",
                schema: "fleet");
        }
    }
}
