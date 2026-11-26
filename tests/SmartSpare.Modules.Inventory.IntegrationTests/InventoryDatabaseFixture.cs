using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSpare.MigrationRunner;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SmartSpare.Modules.Inventory;

/// <summary>A PostgreSQL 17 server bootstrapped and migrated exactly as the migration runner does it.</summary>
public sealed class InventoryDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>The migrator login acting as <c>smartspare_owner</c>, as migrations and the demo-data tools do.</summary>
    public string MigratorConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = NewPassword(), Api = NewPassword(), Jobs = NewPassword(), Reporting = NewPassword() },
        };
        await new DatabaseMigrator(options, ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance)
            .RunAsync(TestContext.Current.CancellationToken);
        MigratorConnectionString = options.BuildMigratorConnectionString();
    }

    internal InventoryDbContext CreateContext() => InventoryDatabase.Instance.CreateContext(MigratorConnectionString);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

[CollectionDefinition(Name)]
public sealed class InventoryDatabaseGroup : ICollectionFixture<InventoryDatabaseFixture>
{
    public const string Name = "Inventory database";
}
