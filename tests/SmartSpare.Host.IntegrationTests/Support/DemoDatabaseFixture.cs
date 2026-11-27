extern alias runner;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using runner::SmartSpare.MigrationRunner;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// PostgreSQL 17 migrated exactly as the migration runner does it, loaded with the T001 demo dataset ("Load demo data")
/// and with the demo users' passwords set by <c>MigrationRunner set-demo-passwords</c>. The host connects as
/// <c>smartspare_api</c>.
/// </summary>
public sealed class DemoDatabaseFixture : IAsyncLifetime
{
    public const string Password = "a long enough demo password";

    /// <summary>A small catalogue is enough: users, hubs and brands do not depend on the number of SKUs.</summary>
    private const int DemoParts = 200;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ApiConnectionString { get; private set; } = null!;

    public string MigratorConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _container.StartAsync(ct);

        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = NewPassword(), Api = NewPassword(), Jobs = NewPassword(), Reporting = NewPassword() },
        };
        await new DatabaseMigrator(options, SmartSpare.ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(ct);
        MigratorConnectionString = options.BuildMigratorConnectionString();
        ApiConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "smartspare",
            Username = "smartspare_api",
            Password = options.Passwords.Api,
        }.ConnectionString;

        var script = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "db", "testdata", "T001__demo_dataset.sql"), ct);
        await _container.CopyAsync(script, "/tmp/T001__demo_dataset.sql", ct: ct);
        var result = await _container.ExecAsync(
            ["bash", "-c", $"PGPASSWORD='{options.Passwords.Migrator}' psql -X -q -v ON_ERROR_STOP=1 -h localhost -U smartspare_migrator -d smartspare -v parts={DemoParts} -f /tmp/T001__demo_dataset.sql"],
            ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"T001 failed: {result.Stderr}");
        }

        await DemoPasswords.SetAsync(MigratorConnectionString, Password, ct);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Id → code of a small lookup table (brands, hubs).</summary>
    public async Task<Dictionary<short, string>> CodesAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var codes = new Dictionary<short, string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            codes[reader.GetInt16(0)] = reader.GetString(1);
        }

        return codes;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    internal static string RepositoryRoot()
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

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

[CollectionDefinition(Name)]
public sealed class DemoDatabaseGroup : ICollectionFixture<DemoDatabaseFixture>
{
    public const string Name = "Demo database";
}
