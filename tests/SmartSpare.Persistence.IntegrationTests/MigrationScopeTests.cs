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

    private static FakeDatabase Db(string name) => new(name);

    private sealed class FakeDatabase(string name) : IModuleDatabase
    {
        public string Name => name;

        public string HistorySchema => name;

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync(string connectionString, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool HasPendingModelChanges() => false;
    }
}
