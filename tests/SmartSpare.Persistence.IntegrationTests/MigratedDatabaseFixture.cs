using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.MigrationRunner;
using Testcontainers.PostgreSql;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>
/// A real PostgreSQL 17 server, bootstrapped (roles, database) and migrated exactly as the
/// migration runner does it locally and in CI: Platform first, then every module context.
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public IReadOnlyList<IModuleDatabase> Databases { get; } = ModuleCatalog.ModuleCatalog.Databases();

    public MigrationRunnerOptions Options { get; private set; } = null!;

    /// <summary>Superuser connection to the smartspare database (the container's admin).</summary>
    public string AdminConnectionString { get; private set; } = null!;

    public string MigratorConnectionString => Options.BuildMigratorConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        Options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords
            {
                Migrator = "test_migrator",
                Api = "test_api",
                Jobs = "test_jobs",
                Reporting = "test_report",
            },
        };
        AdminConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = "smartspare" }.ConnectionString;

        await CreateMigrator().RunAsync(TestContext.Current.CancellationToken);
    }

    public DatabaseMigrator CreateMigrator() => new(Options, Databases, NullLogger<DatabaseMigrator>.Instance);

    public async Task<T> ScalarAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
