using Npgsql;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>Bootstrap + Platform migration on PostgreSQL 17 (PR 2b, MODULE_MAPPING section 6, step 5).</summary>
[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class PlatformMigrationTests(MigratedDatabaseFixture db)
{
    [Fact]
    public async Task EveryContext_HasNoPendingMigrations()
    {
        foreach (var database in db.Databases)
        {
            var pending = await database.GetPendingMigrationsAsync(db.MigratorConnectionString, TestContext.Current.CancellationToken);
            pending.ShouldBeEmpty($"{database.Name} has pending migrations");
        }
    }

    [Fact]
    public async Task SecondRun_SkipsBootstrapAndAppliesNothing()
    {
        await db.CreateMigrator().RunAsync(TestContext.Current.CancellationToken);

        (await db.ScalarAsync<long>("SELECT count(*) FROM util.__ef_migrations_history")).ShouldBe(1);
    }

    [Fact]
    public async Task PlatformHistoryTable_IsInUtilSchema()
    {
        (await db.ScalarAsync<bool>("SELECT to_regclass('util.__ef_migrations_history') IS NOT NULL")).ShouldBeTrue();
        (await db.ScalarAsync<long>("SELECT count(*) FROM util.__ef_migrations_history WHERE migration_id LIKE '%_InitialPlatform'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task PlatformObjects_AreOwnedByTheOwnerRole_NotByTheMigratorLogin()
    {
        // Proves that Options=-c role=smartspare_owner on the migrator connection takes effect.
        var owners = await db.ScalarAsync<string>("""
            SELECT string_agg(DISTINCT pg_get_userbyid(c.relowner)::text, ',')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('ref', 'util', 'audit')
            """);
        owners.ShouldBe("smartspare_owner");
        (await db.ScalarAsync<string>("SELECT current_user", db.MigratorConnectionString)).ShouldBe("smartspare_owner");
    }

    // Row counts of the reference data of the V016 database (the former seed R001), unchanged by the port.
    [Theory]
    [InlineData("unit_of_measure", 5)]
    [InlineData("brand", 8)]
    [InlineData("machine_type", 6)]
    [InlineData("part_category", 31)]
    [InlineData("operating_condition", 4)]
    [InlineData("movement_type", 10)]
    [InlineData("demand_class", 4)]
    [InlineData("season_phase", 6)]
    [InlineData("agro_calendar_week", 53)]
    public async Task ReferenceData_IsSeeded(string table, long expected) =>
        (await db.ScalarAsync<long>($"SELECT count(*) FROM ref.{table}")).ShouldBe(expected);

    [Fact]
    public async Task AuditLog_HasMonthlyPartitionsAndDefault()
    {
        (await db.ScalarAsync<long>("SELECT count(*) FROM pg_inherits WHERE inhparent = 'audit.audit_log'::regclass")).ShouldBe(49);
        (await db.ScalarAsync<bool>("SELECT to_regclass('audit.audit_log_default') IS NOT NULL")).ShouldBeTrue();
        (await db.ScalarAsync<bool>("SELECT to_regclass('audit.audit_log_202501') IS NOT NULL")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("UPDATE audit.audit_log SET reason = 'changed' WHERE table_name = 'test.append_only'")]
    [InlineData("DELETE FROM audit.audit_log WHERE table_name = 'test.append_only'")]
    public async Task AuditLog_RejectsUpdateAndDelete_EvenForTheOwner(string statement)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var insert = new NpgsqlCommand(
            "SET LOCAL ROLE smartspare_owner; "
            + "INSERT INTO audit.audit_log (action, table_name, record_id, new_values) VALUES ('INSERT', 'test.append_only', '1', '{}')",
            connection, transaction))
        {
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

#pragma warning disable CA2100 // Statements are test constants.
        await using var change = new NpgsqlCommand(statement, connection, transaction);
#pragma warning restore CA2100
        var error = await Should.ThrowAsync<PostgresException>(() => change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.ShouldContain("append-only");
    }
}
