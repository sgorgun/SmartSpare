using Npgsql;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>Checks shared by every module context: order, history table, structure (PR 2c).</summary>
public abstract class ModuleMigrationTests(MigratedDatabaseFixture db, string module, string historySchema)
{
    protected MigratedDatabaseFixture Db { get; } = db;

    [Fact]
    public void Context_IsMigratedAfterThePlatform()
    {
        var names = Db.Databases.Select(d => d.Name).ToList();

        names.ShouldContain(module);
        names.IndexOf(module).ShouldBeGreaterThan(names.IndexOf("platform"));
    }

    [Fact]
    public async Task Context_HasNoPendingMigrations()
    {
        var database = Db.Databases.Single(d => d.Name == module);

        (await database.GetPendingMigrationsAsync(Db.MigratorConnectionString, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        database.HistorySchema.ShouldBe(historySchema);
    }

    [Fact]
    public async Task HistoryTable_IsInTheModuleSchema() =>
        (await Db.ScalarAsync<long>($"SELECT count(*) FROM {historySchema}.__ef_migrations_history WHERE migration_id LIKE '%_Initial%'"))
            .ShouldBe(1);

    protected async Task ShouldExistAsync(string kind, string sql, IEnumerable<string> expected)
    {
        var actual = await Db.ScalarAsync<string[]>(sql);
        foreach (var name in expected)
        {
            actual.ShouldContain(name, $"{kind} {name} is missing");
        }
    }

    protected Task TablesShouldExistAsync(params string[] tables) => ShouldExistAsync("table", """
        SELECT coalesce(array_agg(n.nspname || '.' || c.relname), '{}')
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
        """, tables);

    protected Task TriggersShouldExistAsync(params string[] triggers) => ShouldExistAsync("trigger", """
        SELECT coalesce(array_agg(c.relname || '.' || t.tgname), '{}')
        FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
        WHERE NOT t.tgisinternal AND NOT c.relispartition
        """, triggers);

    protected Task FunctionsShouldExistAsync(params string[] functions) => ShouldExistAsync("function", """
        SELECT coalesce(array_agg(n.nspname || '.' || p.proname), '{}')
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        """, functions);

    protected static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
#pragma warning disable CA2100 // Test constants.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class IdentityMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "identity", "iam")
{
    [Fact]
    public async Task Structure_MatchesV016()
    {
        await TablesShouldExistAsync("iam.app_user", "iam.role", "iam.user_role", "iam.user_brand");
        await TriggersShouldExistAsync("app_user.trg_app_user_updated_at", "app_user.trg_audit", "user_role.trg_audit", "user_brand.trg_audit");
        await FunctionsShouldExistAsync("iam.user_has_role", "iam.anonymise_user");
    }

    [Fact]
    public async Task ApplicationRoles_AreSeeded() =>
        (await Db.ScalarAsync<long>("SELECT count(*) FROM iam.role")).ShouldBe(6);

    [Fact]
    public async Task AuditTrigger_WritesTheChange_WithoutPersonalData() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, $"""
                WITH e AS (INSERT INTO iam.employee (code, full_name, position_id)
                           SELECT 'E999901', 'Audit Probe', id FROM iam.position WHERE code = 'INVENTORY_PLANNER' RETURNING id)
                INSERT INTO iam.app_user (employee_id, user_name, email, full_name, password_hash)
                SELECT e.id, 'audit.probe', 'audit.probe@dealer.example', 'Audit Probe', 'AQAAAA{new string('A', 76)}' FROM e
                """);

            await using var check = new NpgsqlCommand("""
                SELECT new_values ? 'user_name' AND NOT new_values ? 'email' AND NOT new_values ? 'full_name'
                FROM audit.audit_log WHERE table_name = 'iam.app_user' AND action = 'INSERT'
                ORDER BY id DESC LIMIT 1
                """, connection, transaction);
            ((bool)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBeTrue();
        });

    [Fact]
    public async Task The_masked_user_directory_is_module_local_with_the_warehouse_id()
    {
        // X18 (#11): created by Identity, returns home_warehouse_id and depends on no other module's schema.
        (await Db.ScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'iam' AND table_name = 'v_app_user_masked' AND column_name = 'home_warehouse_id')
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = 'iam' AND table_name = 'v_app_user_masked' AND column_name = 'home_warehouse_code')
            """)).ShouldBeTrue();
        (await Db.ScalarAsync<string[]>("""
            SELECT coalesce(array_agg(DISTINCT n.nspname::text ORDER BY n.nspname::text), '{}')
            FROM pg_rewrite r
            JOIN pg_depend d ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid AND d.refclassid = 'pg_class'::regclass
            JOIN pg_class c ON c.oid = d.refobjid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE r.ev_class = 'iam.v_app_user_masked'::regclass AND d.refobjid <> r.ev_class
            """)).ShouldBe(["iam"]);
        (await Db.ScalarAsync<long>("SELECT count(*) FROM iam.__ef_migrations_history WHERE migration_id LIKE '%_ModuleLocalMaskedUserView'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_data_protection_key_ring_is_readable_by_the_api_login_only_and_not_audited()
    {
        await TablesShouldExistAsync("iam.data_protection_keys");
        (await Db.ScalarAsync<bool>("""
            SELECT has_table_privilege('app_write', 'iam.data_protection_keys', 'SELECT')
               AND has_table_privilege('app_write', 'iam.data_protection_keys', 'INSERT')
               AND NOT has_table_privilege('app_write', 'iam.data_protection_keys', 'UPDATE')
               AND NOT has_table_privilege('app_write', 'iam.data_protection_keys', 'DELETE')
               AND has_sequence_privilege('app_write', pg_get_serial_sequence('iam.data_protection_keys', 'id'), 'USAGE')
               AND NOT has_table_privilege('app_read', 'iam.data_protection_keys', 'SELECT')
               AND NOT has_table_privilege('smartspare_reporting', 'iam.data_protection_keys', 'SELECT')
            """)).ShouldBeTrue();
        (await Db.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger WHERE tgrelid = 'iam.data_protection_keys'::regclass AND NOT tgisinternal
            """)).ShouldBe(0, "key material must never reach the audit trail");
    }

    [Fact]
    public async Task Upper_case_user_names_violate_the_check() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, transaction, $"""
                WITH e AS (INSERT INTO iam.employee (code, full_name, position_id)
                           SELECT 'E999902', 'Upper Case', id FROM iam.position WHERE code = 'INVENTORY_PLANNER' RETURNING id)
                INSERT INTO iam.app_user (employee_id, user_name, email, full_name, password_hash)
                SELECT e.id, 'Upper.Case', 'upper.case@dealer.example', 'Upper Case', 'AQAAAA{new string('A', 76)}' FROM e
                """));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("ck_app_user_user_name");
        });
}

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class InventoryMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "inventory", "inventory")
{
    [Fact]
    public async Task Structure_MatchesV016()
    {
        await TablesShouldExistAsync(
            "catalog.part", "catalog.part_analogue", "inventory.warehouse", "inventory.warehouse_route", "inventory.warehouse_part",
            "inventory.stock_balance", "inventory.stock_movement", "inventory.stock_reservation", "inventory.demand_exception",
            "inventory.transfer_order", "inventory.transfer_order_line", "raw.import_batch", "raw.part_catalog_row",
            "raw.demand_history_row", "staging.part_catalog", "staging.demand_history", "staging.import_error");
        await TriggersShouldExistAsync(
            "stock_movement.trg_stock_movement_apply", "stock_movement.trg_stock_movement_immutable",
            "stock_reservation.trg_stock_reservation_guard", "stock_reservation.trg_stock_reservation_sync",
            "transfer_order.trg_transfer_order_number", "transfer_order.trg_transfer_order_status_guard",
            "transfer_order_line.trg_transfer_line_editable", "demand_exception.trg_demand_exception_validate", "part.trg_audit");
        await FunctionsShouldExistAsync(
            "inventory.approve_transfer", "inventory.dispatch_transfer", "inventory.receive_transfer", "inventory.cancel_transfer",
            "inventory.expire_soft_reservations", "inventory.flag_exceptional_demand", "inventory.find_exceptional_demand_candidates",
            "staging.process_batch", "staging.validate_demand_history", "staging.load_part_catalog");
    }

    [Fact]
    public async Task WarehousesAndRoutes_AreSeeded()
    {
        (await Db.ScalarAsync<long>("SELECT count(*) FROM inventory.warehouse")).ShouldBe(7);
        (await Db.ScalarAsync<long>("SELECT count(*) FROM inventory.warehouse_route")).ShouldBe(42);
    }

    [Fact]
    public async Task StockLedger_HasMonthlyPartitionsAndDefault()
    {
        (await Db.ScalarAsync<long>("SELECT count(*) FROM pg_inherits WHERE inhparent = 'inventory.stock_movement'::regclass")).ShouldBe(61);
        (await Db.ScalarAsync<bool>("SELECT to_regclass('inventory.stock_movement_202401') IS NOT NULL AND to_regclass('inventory.stock_movement_default') IS NOT NULL"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task IssueWithoutStock_IsRejectedWithSS200() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
                SELECT b.id, 'TEST-SS200', 'Probe part for the ledger trigger', c.id, u.id, 10
                FROM ref.brand b, ref.part_category c, ref.unit_of_measure u
                WHERE b.code = (SELECT min(code) FROM ref.brand) AND c.code = 'CAB_FILTER' AND u.code = 'PCS'
                """);

            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, transaction, """
                INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
                SELECT now(), w.id, p.id, 'ISSUE_SALE', 1, 'TEST-1'
                FROM inventory.warehouse w, catalog.part p WHERE w.code = 'VLN' AND p.oem_part_number = 'TEST-SS200'
                """));

            error.SqlState.ShouldBe("SS200");
        });
}

[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class FleetMigrationTests(MigratedDatabaseFixture db) : ModuleMigrationTests(db, "fleet", "fleet")
{
    [Fact]
    public async Task Structure_MatchesV016()
    {
        await TablesShouldExistAsync(
            "fleet.customer", "fleet.machine_model", "fleet.machine", "fleet.engine_hours_reading", "fleet.maintenance_plan",
            "fleet.maintenance_plan_part", "fleet.service_order", "fleet.service_order_line");
        await TriggersShouldExistAsync(
            "engine_hours_reading.trg_engine_hours_validate", "engine_hours_reading.trg_engine_hours_apply",
            "engine_hours_reading.trg_engine_hours_immutable", "service_order.trg_service_order_number",
            "service_order.trg_service_order_status_guard", "machine.trg_audit");
        await FunctionsShouldExistAsync(
            "fleet.fn_maintenance_due", "fleet.log_engine_hours", "fleet.create_service_order_draft",
            "fleet.complete_service_order", "fleet.cancel_service_order");
        (await Db.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_customer_erp_ref_hash' AND contype = 'u')"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task EngineHours_LowerThanTheLastReading_AreRejectedWithSS100() =>
        await Db.InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO fleet.customer (customer_code, region_warehouse_id) SELECT 'CUST-999001', id FROM inventory.warehouse WHERE code = 'PNV';
                INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
                SELECT (SELECT min(id) FROM ref.brand), (SELECT min(id) FROM ref.machine_type), 'Probe 1000';
                INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year)
                SELECT 'TESTVIN0001', mm.id, c.id, (SELECT min(id) FROM ref.operating_condition), c.region_warehouse_id, 2024
                FROM fleet.machine_model mm, fleet.customer c WHERE mm.model_name = 'Probe 1000' AND c.customer_code = 'CUST-999001';
                INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at)
                SELECT id, 500, now() - interval '2 days' FROM fleet.machine WHERE vin = 'TESTVIN0001';
                """);

            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, transaction, """
                INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at)
                SELECT id, 400, now() - interval '1 day' FROM fleet.machine WHERE vin = 'TESTVIN0001'
                """));

            error.SqlState.ShouldBe("SS100");
        });
}
