using SmartSpare.Infrastructure.Persistence;
using SmartSpare.MigrationRunner;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>The <c>--module</c> filter of the migration runner (ADR 0020 §3).</summary>
public sealed class MigrationScopeTests
{
    private static readonly IReadOnlyList<IModuleDatabase> Catalogue =
        [Db("platform"), Db("identity"), Db("inventory"), Db("fleet")];

    [Fact]
    public async Task Without_a_module_every_database_is_migrated_in_order()
    {
        var scope = await MigrationScope.SelectAsync(Catalogue, null, Pending());

        scope.Select(d => d.Name).ShouldBe(["platform", "identity", "inventory", "fleet"]);
    }

    [Fact]
    public async Task A_module_migrates_the_platform_and_itself()
    {
        var scope = await MigrationScope.SelectAsync(Catalogue, "Inventory", Pending(("platform", 1), ("fleet", 2)));

        scope.Select(d => d.Name).ShouldBe(["platform", "inventory"]);
    }

    [Fact]
    public async Task The_platform_alone_can_be_migrated()
    {
        var scope = await MigrationScope.SelectAsync(Catalogue, "platform", Pending(("identity", 1)));

        scope.Select(d => d.Name).ShouldBe(["platform"]);
    }

    [Fact]
    public async Task An_earlier_module_with_pending_migrations_refuses_the_run()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => MigrationScope.SelectAsync(Catalogue, "fleet", Pending(("identity", 2))));

        error.Message.ShouldContain("identity (2 pending)");
    }

    [Fact]
    public async Task An_unknown_module_is_refused()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(() => MigrationScope.SelectAsync(Catalogue, "billing", Pending()));

        error.Message.ShouldContain("Unknown module 'billing'");
    }

    private static Func<IModuleDatabase, Task<IReadOnlyList<string>>> Pending(params (string Name, int Count)[] pending) =>
        database => Task.FromResult<IReadOnlyList<string>>(
            [.. Enumerable.Repeat("m", pending.FirstOrDefault(p => p.Name == database.Name).Count)]);

    [Fact]
    public void Catalogue_order_holds_except_that_a_listed_migration_waits_for_the_one_it_runs_after()
    {
        // A database at v0.6.0: Identity's step 3 waits for Reporting's step 2 (STAGE_7_PLAN 1.6).
        var platform = Db("platform");
        var identity = Db("identity");
        var inventory = Db("inventory");
        var reporting = Db("reporting");
        var pending = new Dictionary<IModuleDatabase, List<string>>
        {
            [platform] = ["20261127215106_RequirePermission"],
            [identity] = ["20261127145034_Organisation", "20261127154522_ResponsibilityProjection", "20261127163529_DropLegacyAccess", "20261127215037_RequirePermission"],
            [inventory] = ["20261127215123_RequirePermission"],
            [reporting] = ["20261127154602_LegacyAccessToAssignments", "20261127200000_Later"],
        };

        var runs = SmartSpare.MigrationRunner.DatabaseMigrator.Plan([platform, identity, inventory, reporting], pending);

        // Identity waits; Inventory goes on; Reporting applies step 2, Identity resumes, then the rest of Reporting.
        runs.Select(r => (r.Database.Name, r.Target, r.Migrations.Count)).ShouldBe(
        [
            ("platform", "20261127215106_RequirePermission", 1),
            ("identity", "20261127154522_ResponsibilityProjection", 2),
            ("inventory", "20261127215123_RequirePermission", 1),
            ("reporting", "20261127154602_LegacyAccessToAssignments", 1),
            ("identity", "20261127215037_RequirePermission", 2),
            ("reporting", "20261127200000_Later", 1),
        ]);
        pending.Values.ShouldAllBe(p => p.Count == 0);
    }

    [Fact]
    public void Without_the_awaited_migration_pending_or_in_scope_the_order_is_the_catalogue_order()
    {
        var identity = Db("identity");
        var pending = new Dictionary<IModuleDatabase, List<string>> { [identity] = ["20261127163529_DropLegacyAccess", "20261127215037_RequirePermission"] };

        SmartSpare.MigrationRunner.DatabaseMigrator.Plan([identity], pending)
            .ShouldHaveSingleItem().Target.ShouldBe("20261127215037_RequirePermission", "a per-module run (--module identity) is not held back");
    }

    private static FakeDatabase Db(string name) => new(name);

    private sealed class FakeDatabase(string name) : IModuleDatabase
    {
        public string Name => name;

        public string HistorySchema => name;

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync(string connectionString, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MigrateToAsync(string connectionString, string targetMigration, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool HasPendingModelChanges() => false;
    }
}
