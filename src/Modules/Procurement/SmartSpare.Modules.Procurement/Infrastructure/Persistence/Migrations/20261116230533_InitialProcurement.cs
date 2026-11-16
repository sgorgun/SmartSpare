using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;
using SmartSpare.Modules.Procurement.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Procurement tables as of V016, with the supplier tables moved from catalog (ADR 0011 D2).
    /// EF creates what it models; the embedded scripts in db/sql/ add what it cannot.
    /// </summary>
    internal partial class InitialProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "procurement");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:procurement.po_status", "draft,pending_approval,approved,exported,partially_received,received,cancelled")
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "procurement",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Id of the handled event."),
                    consumer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, comment: "Handler that consumed the event."),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the event was handled.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.message_id, x.consumer });
                },
                comment: "Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, comment: "Event id; receivers deduplicate by it."),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "Time the event occurred."),
                    type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, comment: "CLR type of the event (Namespace.Type, Assembly)."),
                    payload = table.Column<string>(type: "jsonb", nullable: false, comment: "Event serialized as a JSON object."),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time the event was delivered to every consumer; NULL = pending."),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0, comment: "Failed delivery attempts so far."),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, comment: "Last delivery error, if any.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.id);
                    table.CheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_message_payload", "jsonb_typeof(payload) = 'object'");
                },
                comment: "Integration events of this module waiting for delivery (transactional outbox, ADR 0003).");

            migrationBuilder.CreateTable(
                name: "supplier",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Business code (natural key)."),
                    name = table.Column<string>(type: "character varying(200)", nullable: false, comment: "Display name."),
                    country_code = table.Column<string>(type: "character(2)", nullable: false),
                    order_email = table.Column<string>(type: "citext", nullable: true, comment: "Shared order mailbox of the company (e.g. orders@...), not a personal address."),
                    default_lead_time_days = table.Column<short>(type: "smallint", nullable: false),
                    currency_code = table.Column<string>(type: "character(3)", nullable: false, defaultValueSql: "'EUR'::bpchar"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true", comment: "FALSE = deactivated, kept for history."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier", x => x.id);
                    table.UniqueConstraint("uq_supplier_code", x => x.code);
                    table.UniqueConstraint("uq_supplier_name", x => x.name);
                    table.CheckConstraint("ck_supplier_country", "(country_code ~ '^[A-Z]{2}$'::text)");
                    table.CheckConstraint("ck_supplier_currency", "(currency_code ~ '^[A-Z]{3}$'::text)");
                    table.CheckConstraint("ck_supplier_email", "((order_email IS NULL) OR (order_email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext))");
                    table.CheckConstraint("ck_supplier_lead_time", "((default_lead_time_days >= 1) AND (default_lead_time_days <= 365))");
                },
                comment: "OEM suppliers / distributors. Business contact data only (no personal data).");

            migrationBuilder.CreateTable(
                name: "purchase_order",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(20)", nullable: false, comment: "Business document number (assigned by trigger)."),
                    supplier_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to procurement.supplier."),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse."),
                    status = table.Column<PoStatus>(type: "procurement.po_status", nullable: false, defaultValueSql: "'draft'::procurement.po_status", comment: "Workflow status (state machine guarded by trigger)."),
                    is_urgent = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "US16 emergency direct purchase (\"Urgent Harvest Dispatch\"), bypasses weekly batching."),
                    urgent_reason = table.Column<string>(type: "character varying(300)", nullable: true),
                    currency_code = table.Column<string>(type: "character(3)", nullable: false, defaultValueSql: "'EUR'::bpchar"),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValueSql: "0", comment: "Controlled redundancy: SUM(line_amount), maintained by trigger; needed by the four-eyes CHECK."),
                    expected_arrival_date = table.Column<DateOnly>(type: "date", nullable: true, comment: "Approval date + longest supplier lead time of the lines."),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false, comment: "User who created the record (iam.app_user)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the submitted event."),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the approved event."),
                    exported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the exported event."),
                    cancel_reason = table.Column<string>(type: "character varying(300)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_order", x => x.id);
                    table.UniqueConstraint("uq_purchase_order_number", x => x.number);
                    table.CheckConstraint("ck_purchase_order_approved", "((status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'cancelled'::procurement.po_status])) OR ((approved_by IS NOT NULL) AND (approved_at IS NOT NULL)))");
                    table.CheckConstraint("ck_purchase_order_cancel", "((status = 'cancelled'::procurement.po_status) = (cancel_reason IS NOT NULL))");
                    table.CheckConstraint("ck_purchase_order_currency", "(currency_code ~ '^[A-Z]{3}$'::text)");
                    table.CheckConstraint("ck_purchase_order_four_eyes", "((approved_by IS NULL) OR (total_amount <= (5000)::numeric) OR (approved_by <> created_by))");
                    table.CheckConstraint("ck_purchase_order_number", "((number)::text ~ '^PO-\\d{4}-\\d{6}$'::text)");
                    table.CheckConstraint("ck_purchase_order_timeline", "((approved_at IS NULL) OR (approved_at >= created_at))");
                    table.CheckConstraint("ck_purchase_order_total", "(total_amount >= (0)::numeric)");
                    table.CheckConstraint("ck_purchase_order_urgent", "((NOT is_urgent) OR (urgent_reason IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_purchase_order_supplier",
                        column: x => x.supplier_id,
                        principalSchema: "procurement",
                        principalTable: "supplier",
                        principalColumn: "id");
                },
                comment: "Supplier purchase order (PO-YYYY-NNNNNN). Drafts are generated from recommendations and approved by management (US14).");

            migrationBuilder.CreateTable(
                name: "supplier_part",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    supplier_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to procurement.supplier."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    supplier_part_number = table.Column<string>(type: "character varying(40)", nullable: true),
                    moq = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "1", comment: "Minimum order quantity; must be a multiple of pack_size."),
                    pack_size = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "1", comment: "Packaging increment; ordered quantities must be multiples of it."),
                    lead_time_days = table.Column<short>(type: "smallint", nullable: false, comment: "Supplier lead time for this part (calendar days)."),
                    is_preferred = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false", comment: "Preferred supplier used for automatic PO drafts (max. one per part)."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created."),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the last change (maintained by trigger).")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_part", x => x.id);
                    table.UniqueConstraint("uq_supplier_part", x => new { x.supplier_id, x.part_id });
                    table.CheckConstraint("ck_supplier_part_lead_time", "((lead_time_days >= 1) AND (lead_time_days <= 365))");
                    table.CheckConstraint("ck_supplier_part_moq", "(moq > (0)::numeric)");
                    table.CheckConstraint("ck_supplier_part_moq_pack", "(mod(moq, pack_size) = (0)::numeric)");
                    table.CheckConstraint("ck_supplier_part_pack", "(pack_size > (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_supplier_part_supplier",
                        column: x => x.supplier_id,
                        principalSchema: "procurement",
                        principalTable: "supplier",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Commercial conditions of a supplier for a part: MOQ, pack size, lead time (US12).");

            migrationBuilder.CreateTable(
                name: "purchase_order_line",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    purchase_order_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to procurement.purchase_order."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to catalog.part."),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Quantity in the unit of measure of the part."),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    line_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true, computedColumnSql: "round((quantity * unit_price), 2)", stored: true, comment: "Generated column: round(quantity x unit_price, 2)."),
                    received_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValueSql: "0", comment: "Quantity received so far (partial receipts)."),
                    proposed_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    override_reason = table.Column<string>(type: "character varying(300)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_order_line", x => x.id);
                    table.UniqueConstraint("uq_purchase_order_line_part", x => new { x.purchase_order_id, x.part_id });
                    table.CheckConstraint("ck_purchase_order_line_override", "((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL))");
                    table.CheckConstraint("ck_purchase_order_line_price", "(unit_price > (0)::numeric)");
                    table.CheckConstraint("ck_purchase_order_line_qty", "(quantity > (0)::numeric)");
                    table.CheckConstraint("ck_purchase_order_line_received", "((received_qty >= (0)::numeric) AND (received_qty <= quantity))");
                    table.ForeignKey(
                        name: "fk_purchase_order_line_order",
                        column: x => x.purchase_order_id,
                        principalSchema: "procurement",
                        principalTable: "purchase_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "PO line. MOQ / pack-size rules of procurement.supplier_part are enforced by trigger.");

            migrationBuilder.CreateTable(
                name: "supplier_price",
                schema: "procurement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    supplier_part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to procurement.supplier_part."),
                    valid_during = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false, comment: "Validity period [from, to); open upper bound = valid until further notice."),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_price", x => x.id);
                    table.CheckConstraint("ck_supplier_price_amount", "(unit_price > (0)::numeric)");
                    table.CheckConstraint("ck_supplier_price_range", "((NOT isempty(valid_during)) AND (lower(valid_during) IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_supplier_price_supplier_part",
                        column: x => x.supplier_part_id,
                        principalSchema: "procurement",
                        principalTable: "supplier_part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Supplier purchase price history; non-overlapping validity periods per supplier-part (EXCLUDE USING gist).");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "procurement",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_approved_by",
                schema: "procurement",
                table: "purchase_order",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_created_by",
                schema: "procurement",
                table: "purchase_order",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_open",
                schema: "procurement",
                table: "purchase_order",
                columns: new[] { "status", "expected_arrival_date" },
                filter: "(status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'approved'::procurement.po_status, 'exported'::procurement.po_status, 'partially_received'::procurement.po_status]))");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_supplier_created",
                schema: "procurement",
                table: "purchase_order",
                columns: new[] { "supplier_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_warehouse",
                schema: "procurement",
                table: "purchase_order",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_line_part",
                schema: "procurement",
                table: "purchase_order_line",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_part_part",
                schema: "procurement",
                table: "supplier_part",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "uq_supplier_part_preferred",
                schema: "procurement",
                table: "supplier_part",
                column: "part_id",
                unique: true,
                filter: "is_preferred");

            // Supplier tables moved from catalog: EXCLUDE constraint, triggers (V003, V015; ADR 0011 D2).
            migrationBuilder.SqlResource(typeof(InitialProcurement).Assembly, "procurement/0001_suppliers.sql");

            // Purchase order rules, totals and approval workflow (V007, V011, V012, V015).
            migrationBuilder.SqlResource(typeof(InitialProcurement).Assembly, "procurement/0002_purchase_orders.sql");

            // Privileges on procurement objects, outbox and inbox (V015, MODULE_MAPPING 4.3).
            migrationBuilder.SqlResource(typeof(InitialProcurement).Assembly, "procurement/0003_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "purchase_order_line",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "supplier_price",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "purchase_order",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "supplier_part",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "supplier",
                schema: "procurement");
        }
    }
}
