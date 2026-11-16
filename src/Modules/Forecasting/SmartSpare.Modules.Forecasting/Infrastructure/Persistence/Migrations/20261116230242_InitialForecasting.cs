using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSpare.Infrastructure.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SmartSpare.Modules.Forecasting.Infrastructure.Persistence;

#nullable disable

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Forecasting tables as of V016 (ADR 0011, MODULE_MAPPING 1.6).
    /// EF creates what it models; the embedded scripts in db/sql/ add what it cannot.
    /// </summary>
    internal partial class InitialForecasting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "forecast");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:forecast.forecast_method", "zero,ma3,ses,croston,sba,fasttree")
                .Annotation("Npgsql:Enum:forecast.model_status", "training,candidate,active,retired,rejected")
                .Annotation("Npgsql:Enum:forecast.recommendation_kind", "transfer,purchase")
                .Annotation("Npgsql:Enum:forecast.recommendation_status", "open,accepted,overridden,rejected,expired")
                .Annotation("Npgsql:Enum:forecast.run_status", "running,succeeded,failed");

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "forecast",
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
                name: "ml_model_version",
                schema: "forecast",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    algorithm = table.Column<ForecastMethod>(type: "forecast.forecast_method", nullable: false),
                    version_label = table.Column<string>(type: "character varying(30)", nullable: false),
                    demand_class_code = table.Column<string>(type: "character varying(15)", nullable: true, comment: "Reference to ref.demand_class."),
                    status = table.Column<ModelStatus>(type: "forecast.model_status", nullable: false, defaultValueSql: "'training'::forecast.model_status", comment: "Workflow status (state machine guarded by trigger)."),
                    training_from = table.Column<DateOnly>(type: "date", nullable: false),
                    training_to = table.Column<DateOnly>(type: "date", nullable: false),
                    holdout_from = table.Column<DateOnly>(type: "date", nullable: false),
                    holdout_to = table.Column<DateOnly>(type: "date", nullable: false),
                    rmsse = table.Column<decimal>(type: "numeric(8,4)", nullable: true, comment: "Root mean squared scaled error on the holdout period (KPI-02 metric)."),
                    mase = table.Column<decimal>(type: "numeric(8,4)", nullable: true, comment: "Mean absolute scaled error on the holdout period."),
                    mae = table.Column<decimal>(type: "numeric(12,4)", nullable: true),
                    rmse = table.Column<decimal>(type: "numeric(12,4)", nullable: true),
                    hyperparameters = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb", comment: "Algorithm-specific settings as a JSON object, e.g. {\"numberOfTrees\":200,\"numberOfLeaves\":31,\"learningRate\":0.1}."),
                    artifact_uri = table.Column<string>(type: "character varying(500)", nullable: true, comment: "Location of the serialised ML.NET model (.zip) in Azure Blob Storage."),
                    artifact_sha256 = table.Column<string>(type: "character(64)", nullable: true),
                    trained_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the trained event."),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the promoted event."),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the retired event."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ml_model_version", x => x.id);
                    table.UniqueConstraint("uq_ml_model_version_label", x => new { x.algorithm, x.version_label });
                    table.CheckConstraint("ck_ml_model_version_active", "((status <> 'active'::forecast.model_status) OR (promoted_at IS NOT NULL))");
                    table.CheckConstraint("ck_ml_model_version_artifact", "((algorithm <> 'fasttree'::forecast.forecast_method) OR (status = 'training'::forecast.model_status) OR (artifact_uri IS NOT NULL))");
                    table.CheckConstraint("ck_ml_model_version_chronological_holdout", "((holdout_from > training_to) AND (holdout_to >= holdout_from))");
                    table.CheckConstraint("ck_ml_model_version_evaluated", "((status = ANY (ARRAY['training'::forecast.model_status, 'rejected'::forecast.model_status])) OR ((rmsse IS NOT NULL) AND (trained_at IS NOT NULL)))");
                    table.CheckConstraint("ck_ml_model_version_hyperparameters", "(jsonb_typeof(hyperparameters) = 'object'::text)");
                    table.CheckConstraint("ck_ml_model_version_label", "((version_label)::text ~ '^v\\d+\\.\\d+(\\.\\d+)?$'::text)");
                    table.CheckConstraint("ck_ml_model_version_metrics", "(((rmsse IS NULL) OR (rmsse >= (0)::numeric)) AND ((mase IS NULL) OR (mase >= (0)::numeric)) AND ((mae IS NULL) OR (mae >= (0)::numeric)) AND ((rmse IS NULL) OR (rmse >= (0)::numeric)))");
                    table.CheckConstraint("ck_ml_model_version_sha", "((artifact_sha256 IS NULL) OR (artifact_sha256 ~ '^[0-9a-f]{64}$'::text))");
                    table.CheckConstraint("ck_ml_model_version_training", "(training_to > training_from)");
                    table.ForeignKey(
                        name: "fk_ml_model_version_class",
                        column: x => x.demand_class_code,
                        principalSchema: "ref",
                        principalTable: "demand_class",
                        principalColumn: "code");
                },
                comment: "Versioned ML / benchmark models. A candidate is promoted to active only if it beats the best benchmark (forecast.promote_model).");

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "forecast",
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
                name: "benchmark_result",
                schema: "forecast",
                columns: table => new
                {
                    model_version_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to forecast.ml_model_version."),
                    method = table.Column<ForecastMethod>(type: "forecast.forecast_method", nullable: false),
                    demand_class_code = table.Column<string>(type: "character varying(15)", nullable: false, comment: "Reference to ref.demand_class."),
                    series_count = table.Column<int>(type: "integer", nullable: false),
                    rmsse = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    mase = table.Column<decimal>(type: "numeric(8,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_benchmark_result", x => new { x.model_version_id, x.method, x.demand_class_code });
                    table.CheckConstraint("ck_benchmark_result_metrics", "((rmsse >= (0)::numeric) AND (mase >= (0)::numeric))");
                    table.CheckConstraint("ck_benchmark_result_series", "(series_count > 0)");
                    table.ForeignKey(
                        name: "fk_benchmark_result_class",
                        column: x => x.demand_class_code,
                        principalSchema: "ref",
                        principalTable: "demand_class",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_benchmark_result_model",
                        column: x => x.model_version_id,
                        principalSchema: "forecast",
                        principalTable: "ml_model_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Holdout accuracy of a candidate model and of the 5 benchmarks per demand class (KPI-02: ML must have >= 10% lower RMSSE).");

            migrationBuilder.CreateTable(
                name: "forecast_run",
                schema: "forecast",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    model_version_id = table.Column<int>(type: "integer", nullable: false, comment: "Reference to forecast.ml_model_version."),
                    status = table.Column<RunStatus>(type: "forecast.run_status", nullable: false, defaultValueSql: "'running'::forecast.run_status", comment: "Workflow status (state machine guarded by trigger)."),
                    horizon_weeks = table.Column<short>(type: "smallint", nullable: false),
                    first_week = table.Column<DateOnly>(type: "date", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(10)", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who created the record (iam.app_user)."),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time of the started event."),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the finished event."),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forecast_run", x => x.id);
                    table.CheckConstraint("ck_forecast_run_error", "((status = 'failed'::forecast.run_status) = (error_message IS NOT NULL))");
                    table.CheckConstraint("ck_forecast_run_finished", "((status = 'running'::forecast.run_status) = (finished_at IS NULL))");
                    table.CheckConstraint("ck_forecast_run_first_week_monday", "(EXTRACT(isodow FROM first_week) = (1)::numeric)");
                    table.CheckConstraint("ck_forecast_run_horizon", "((horizon_weeks >= 4) AND (horizon_weeks <= 12))");
                    table.CheckConstraint("ck_forecast_run_timeline", "((finished_at IS NULL) OR (finished_at >= started_at))");
                    table.CheckConstraint("ck_forecast_run_trigger", "triggered_by IN ('scheduler', 'user')");
                    table.CheckConstraint("ck_forecast_run_user", "(((triggered_by)::text = 'scheduler'::text) OR (requested_by IS NOT NULL))");
                    table.ForeignKey(
                        name: "fk_forecast_run_model",
                        column: x => x.model_version_id,
                        principalSchema: "forecast",
                        principalTable: "ml_model_version",
                        principalColumn: "id");
                },
                comment: "One execution of the forecasting pipeline (Quartz.NET weekly or user-triggered, horizon 4-12 weeks).");

            migrationBuilder.CreateTable(
                name: "demand_forecast",
                schema: "forecast",
                columns: table => new
                {
                    forecast_run_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to forecast.forecast_run."),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false, comment: "Monday of the forecast ISO week."),
                    forecast_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demand_forecast", x => new { x.forecast_run_id, x.warehouse_id, x.part_id, x.week_start });
                    table.CheckConstraint("ck_demand_forecast_monday", "(EXTRACT(isodow FROM week_start) = (1)::numeric)");
                    table.CheckConstraint("ck_demand_forecast_qty", "(forecast_qty >= (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_demand_forecast_run",
                        column: x => x.forecast_run_id,
                        principalSchema: "forecast",
                        principalTable: "forecast_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Weekly forecast quantity per run, warehouse and part (ternary relationship run x warehouse x part, plus week).");

            migrationBuilder.CreateTable(
                name: "recommendation",
                schema: "forecast",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false, comment: "Surrogate primary key.")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    forecast_run_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to forecast.forecast_run."),
                    kind = table.Column<RecommendationKind>(type: "forecast.recommendation_kind", nullable: false),
                    warehouse_id = table.Column<short>(type: "smallint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    part_id = table.Column<long>(type: "bigint", nullable: false, comment: "Reference to inventory.warehouse_part."),
                    donor_warehouse_id = table.Column<short>(type: "smallint", nullable: true, comment: "Reference to inventory.warehouse."),
                    supplier_id = table.Column<int>(type: "integer", nullable: true, comment: "Reference to procurement.supplier."),
                    base_demand_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "NFR08 (a): ML forecast over the horizon."),
                    maintenance_demand_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "NFR08 (b): planned maintenance demand not yet reserved."),
                    lead_time_buffer_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "NFR08 (c): demand expected during the replenishment lead time."),
                    safety_stock_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "NFR08 (d): safety stock of the hub."),
                    available_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    in_transit_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    net_position_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false, comment: "Available + in transit - (a + b + c + d); negative = shortage."),
                    recommended_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    estimated_cost_eur = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    alternative_cost_eur = table.Column<decimal>(type: "numeric(12,2)", nullable: true, comment: "Cost of the alternative (e.g. emergency supplier order EUR 280 vs. transfer EUR 45, UC01 step 9)."),
                    status = table.Column<RecommendationStatus>(type: "forecast.recommendation_status", nullable: false, defaultValueSql: "'open'::forecast.recommendation_status", comment: "Workflow status (state machine guarded by trigger)."),
                    final_qty = table.Column<decimal>(type: "numeric(12,3)", nullable: true, comment: "Quantity decided by the user (KPI-04: accepted when equal to recommended_qty)."),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true, comment: "User who performed the step (iam.app_user)."),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "Time of the decided event."),
                    decision_reason = table.Column<string>(type: "character varying(300)", nullable: true),
                    transfer_order_id = table.Column<long>(type: "bigint", nullable: true, comment: "Reference to inventory.transfer_order."),
                    purchase_order_id = table.Column<long>(type: "bigint", nullable: true, comment: "Reference to procurement.purchase_order."),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()", comment: "Time the record was created.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recommendation", x => x.id);
                    table.CheckConstraint("ck_recommendation_accepted", "((status <> 'accepted'::forecast.recommendation_status) OR (final_qty = recommended_qty))");
                    table.CheckConstraint("ck_recommendation_cost", "((estimated_cost_eur >= (0)::numeric) AND ((alternative_cost_eur IS NULL) OR (alternative_cost_eur >= (0)::numeric)))");
                    table.CheckConstraint("ck_recommendation_decided", "((status = ANY (ARRAY['open'::forecast.recommendation_status, 'expired'::forecast.recommendation_status])) = (decided_at IS NULL))");
                    table.CheckConstraint("ck_recommendation_drivers", "((base_demand_qty >= (0)::numeric) AND (maintenance_demand_qty >= (0)::numeric) AND (lead_time_buffer_qty >= (0)::numeric) AND (safety_stock_qty >= (0)::numeric) AND (available_qty >= (0)::numeric) AND (in_transit_qty >= (0)::numeric))");
                    table.CheckConstraint("ck_recommendation_final_qty", "((status <> ALL (ARRAY['accepted'::forecast.recommendation_status, 'overridden'::forecast.recommendation_status])) OR ((final_qty > (0)::numeric) AND (decided_by IS NOT NULL)))");
                    table.CheckConstraint("ck_recommendation_kind_purchase", "((kind <> 'purchase'::forecast.recommendation_kind) OR ((supplier_id IS NOT NULL) AND (donor_warehouse_id IS NULL)))");
                    table.CheckConstraint("ck_recommendation_kind_transfer", "((kind <> 'transfer'::forecast.recommendation_kind) OR ((donor_warehouse_id IS NOT NULL) AND (supplier_id IS NULL) AND (donor_warehouse_id <> warehouse_id)))");
                    table.CheckConstraint("ck_recommendation_net_position", "(net_position_qty = ((available_qty + in_transit_qty) - (((base_demand_qty + maintenance_demand_qty) + lead_time_buffer_qty) + safety_stock_qty)))");
                    table.CheckConstraint("ck_recommendation_override", "((status <> ALL (ARRAY['overridden'::forecast.recommendation_status, 'rejected'::forecast.recommendation_status])) OR (decision_reason IS NOT NULL))");
                    table.CheckConstraint("ck_recommendation_override_qty", "((status <> 'overridden'::forecast.recommendation_status) OR (final_qty <> recommended_qty))");
                    table.CheckConstraint("ck_recommendation_qty", "(recommended_qty > (0)::numeric)");
                    table.ForeignKey(
                        name: "fk_recommendation_run",
                        column: x => x.forecast_run_id,
                        principalSchema: "forecast",
                        principalTable: "forecast_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Explainable transfer / purchase proposal. Stores every driver of the calculation (NFR08) and the human decision (KPI-04).");

            migrationBuilder.CreateIndex(
                name: "ix_benchmark_result_class",
                schema: "forecast",
                table: "benchmark_result",
                column: "demand_class_code");

            migrationBuilder.CreateIndex(
                name: "ix_demand_forecast_part_wh_week",
                schema: "forecast",
                table: "demand_forecast",
                columns: new[] { "part_id", "warehouse_id", "week_start" });

            migrationBuilder.CreateIndex(
                name: "ix_forecast_run_latest",
                schema: "forecast",
                table: "forecast_run",
                column: "started_at",
                descending: new bool[0],
                filter: "(status = 'succeeded'::forecast.run_status)");

            migrationBuilder.CreateIndex(
                name: "ix_forecast_run_model",
                schema: "forecast",
                table: "forecast_run",
                column: "model_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_forecast_run_user",
                schema: "forecast",
                table: "forecast_run",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_ml_model_version_class",
                schema: "forecast",
                table: "ml_model_version",
                column: "demand_class_code");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_pending",
                schema: "forecast",
                table: "outbox_message",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_decided_by",
                schema: "forecast",
                table: "recommendation",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_donor",
                schema: "forecast",
                table: "recommendation",
                column: "donor_warehouse_id",
                filter: "(donor_warehouse_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_open",
                schema: "forecast",
                table: "recommendation",
                columns: new[] { "kind", "created_at" },
                filter: "(status = 'open'::forecast.recommendation_status)");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_po",
                schema: "forecast",
                table: "recommendation",
                column: "purchase_order_id",
                filter: "(purchase_order_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_run",
                schema: "forecast",
                table: "recommendation",
                column: "forecast_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_supplier",
                schema: "forecast",
                table: "recommendation",
                column: "supplier_id",
                filter: "(supplier_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_transfer",
                schema: "forecast",
                table: "recommendation",
                column: "transfer_order_id",
                filter: "(transfer_order_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_recommendation_wh_part",
                schema: "forecast",
                table: "recommendation",
                columns: new[] { "warehouse_id", "part_id" });

            // Model registry, promotion, recommendation decisions (V008, V012, V014, V015).
            migrationBuilder.SqlResource(typeof(InitialForecasting).Assembly, "forecasting/0001_forecasting.sql");

            // Privileges on forecast objects, outbox and inbox (V015, MODULE_MAPPING 4.3).
            migrationBuilder.SqlResource(typeof(InitialForecasting).Assembly, "forecasting/0002_grants.sql");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "benchmark_result",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "demand_forecast",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "recommendation",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "forecast_run",
                schema: "forecast");

            migrationBuilder.DropTable(
                name: "ml_model_version",
                schema: "forecast");
        }
    }
}
