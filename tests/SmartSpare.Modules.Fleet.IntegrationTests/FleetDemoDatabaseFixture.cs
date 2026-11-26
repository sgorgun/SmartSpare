using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSpare.MigrationRunner;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// A PostgreSQL 17 server migrated exactly as the migration runner does it and loaded with the T001 demo dataset
/// (500 machines with reading histories, the UC02 Lexion, drafted and completed service orders).
/// </summary>
public sealed class FleetDemoDatabaseFixture : IAsyncLifetime
{
    /// <summary>A small catalogue is enough: the fleet part of T001 does not depend on the number of SKUs.</summary>
    private const int DemoParts = 200;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>The migrator login acting as <c>smartspare_owner</c>, as migrations and the demo-data tools do.</summary>
    public string MigratorConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _container.StartAsync(ct);

        var migratorPassword = NewPassword();
        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = migratorPassword, Api = NewPassword(), Jobs = NewPassword(), Reporting = NewPassword() },
        };
        await new DatabaseMigrator(options, ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(ct);
        MigratorConnectionString = options.BuildMigratorConnectionString();

        var script = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "db", "testdata", "T001__demo_dataset.sql"), ct);
        await _container.CopyAsync(script, "/tmp/T001__demo_dataset.sql", ct: ct);
        var database = new NpgsqlConnectionStringBuilder(MigratorConnectionString).Database;
        var result = await _container.ExecAsync(
            ["bash", "-c", $"PGPASSWORD='{migratorPassword}' psql -X -q -v ON_ERROR_STOP=1 -h localhost -U smartspare_migrator -d {database} -v parts={DemoParts} -f /tmp/T001__demo_dataset.sql"],
            ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"T001 failed: {result.Stderr}");
        }
    }

    internal FleetDbContext CreateContext() => FleetDatabase.Instance.CreateContext(MigratorConnectionString);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartSpare.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("SmartSpare.slnx not found above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition(Name)]
public sealed class FleetDemoDatabaseGroup : ICollectionFixture<FleetDemoDatabaseFixture>
{
    public const string Name = "Fleet demo database";
}
