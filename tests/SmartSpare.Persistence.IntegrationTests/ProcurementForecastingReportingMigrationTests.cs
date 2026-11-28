using Npgsql;

namespace SmartSpare.Persistence.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class ProcurementMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "procurement", "procurement")
{
    [Fact]
    public async Task Structure_MatchesV016_WithSuppliersMovedToProcurement()
    {
        await TablesShouldExistAsync(
            "procurement.supplier", "procurement.supplier_part", "procurement.supplier_price",
            "procurement.purchase_order", "procurement.purchase_order_line", "procurement.outbox_message", "procurement.inbox_message");
        await TriggersShouldExistAsync(
            "supplier.trg_supplier_updated_at", "supplier_part.trg_audit", "purchase_order.trg_purchase_order_number",
            "purchase_order_line.trg_po_line_validate", "purchase_order_line.trg_po_line_total", "purchase_order.trg_po_status_guard");
        await FunctionsShouldExistAsync(
            "procurement.approve_purchase_order", "procurement.receive_purchase_order_line", "procurement.trg_po_line_validate");
        (await Db.ScalarAsync<bool>("SELECT to_regclass('catalog.supplier') IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task OverlappingPricePeriods_AreRejectedByTheExcludeConstraint() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
                SELECT b.id, 'TEST-EXCL', 'Probe part for price periods', c.id, u.id, 10
                FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
                WHERE b.code = (SELECT min(code) FROM catalog.brand) AND c.code = 'CAB_FILTER' AND u.code = 'PCS';
                INSERT INTO procurement.supplier (code, name, country_code, default_lead_time_days) VALUES ('TEST-SUP', 'Probe supplier', 'LT', 7);
                INSERT INTO procurement.supplier_part (supplier_id, part_id, lead_time_days)
                SELECT s.id, p.id, 7 FROM procurement.supplier s, catalog.part p WHERE s.code = 'TEST-SUP' AND p.oem_part_number = 'TEST-EXCL';
                INSERT INTO procurement.supplier_price (supplier_part_id, valid_during, unit_price)
                SELECT id, daterange(current_date, current_date + 30), 10 FROM procurement.supplier_part;
                """);

            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, transaction, """
                INSERT INTO procurement.supplier_price (supplier_part_id, valid_during, unit_price)
                SELECT id, daterange(current_date + 10, current_date + 40), 11 FROM procurement.supplier_part
                """));

            error.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
            error.ConstraintName.ShouldBe("ex_supplier_price_no_overlap");
        });
}

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class ForecastingMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "forecasting", "forecast")
{
    [Fact]
    public async Task Structure_MatchesV016()
    {
        await TablesShouldExistAsync(
            "forecast.ml_model_version", "forecast.benchmark_result", "forecast.forecast_run", "forecast.demand_forecast",
            "forecast.recommendation", "forecast.outbox_message", "forecast.inbox_message");
        await TriggersShouldExistAsync("ml_model_version.trg_model_status_guard", "ml_model_version.trg_audit", "recommendation.trg_audit");
        await FunctionsShouldExistAsync(
            "forecast.promote_model", "forecast.decide_recommendation", "forecast.fn_classify_demand", "forecast.expire_open_recommendations");
        (await Db.ScalarAsync<bool>("SELECT to_regclass('forecast.uq_ml_model_version_one_active') IS NOT NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task ModelStatus_CannotJumpFromTrainingToActive() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO forecast.ml_model_version (algorithm, version_label, training_from, training_to, holdout_from, holdout_to)
                VALUES ('ses', 'v9.9', DATE '2024-01-01', DATE '2025-01-01', DATE '2025-01-08', DATE '2025-06-30')
                """);

            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, transaction,
                "UPDATE forecast.ml_model_version SET status = 'active', promoted_at = now() WHERE version_label = 'v9.9'"));

            error.SqlState.ShouldBe("SS300");
        });
}

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class ReportingMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "reporting", "mart")
{
    [Fact]
    public void Reporting_IsTheLastContext() =>
        Db.Databases[^1].Name.ShouldBe("reporting");

    [Fact]
    public async Task MartsAndTransitionalObjects_Exist()
    {
        await TablesShouldExistAsync("mart.refresh_log");
        (await Db.ScalarAsync<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname || '.' || c.relname IN ('mart.mv_weekly_demand', 'mart.mv_demand_profile', 'mart.v_ml_training_set',
                'mart.v_kpi_dead_stock', 'mart.v_data_quality', 'audit.v_audit_log_masked',
                'inventory.v_in_transit', 'inventory.v_stock_overview', 'fleet.v_machine_maintenance_status', 'fleet.v_service_kit_shortage',
                'procurement.v_purchase_order_overview', 'forecast.v_recommendation_explained')
            """)).ShouldBe(12);     // iam.v_app_user_masked moved to Identity in PR 3c (X18)
        await FunctionsShouldExistAsync("inventory.fn_in_transit_qty", "forecast.fn_stock_position", "mart.refresh_all");
        // InitialReporting, TransitionalCrossModuleObjects and DataQualityParkedOutboxEvents (#31).
        (await Db.ScalarAsync<long>("SELECT count(*) FROM mart.__ef_migrations_history")).ShouldBe(7, "InitialReporting, the transitional objects, the data-quality outbox view, the one-time legacy access copy (STAGE_7_PLAN 1.6), the audit actor from the employee, the drop of the account's personal columns and the part archive in the data-quality view (PR 7i1)");
    }

    [Fact]
    public async Task DataQualityChecks_AreAllZero_OnAnEmptyDatabase() =>
        (await Db.ScalarAsync<long>("SELECT sum(failed_rows)::bigint FROM mart.v_data_quality")).ShouldBe(0);

    [Fact]
    public async Task PrivilegeSelfCheck_HasNoFindings_NowThatEverySchemaExists() =>
        (await Db.ScalarAsync<long>("SELECT count(*) FROM util.v_privilege_findings")).ShouldBe(0);
}
