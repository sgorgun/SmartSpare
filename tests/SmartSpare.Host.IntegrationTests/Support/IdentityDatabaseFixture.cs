extern alias runner;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartSpare.Modules.Identity;
using Testcontainers.PostgreSql;
using runner::SmartSpare.MigrationRunner;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// PostgreSQL 17, bootstrapped and migrated exactly as the migration runner does it. The host connects as the
/// application login <c>smartspare_api</c> (only its real privileges); test data is written as the migrator.
/// </summary>
public sealed class IdentityDatabaseFixture : IAsyncLifetime
{
    public const string Password = "a long enough demo password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ApiConnectionString { get; private set; } = null!;

    public string MigratorConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = NewPassword(), Api = NewPassword(), Jobs = NewPassword(), Reporting = NewPassword() },
        };
        await new DatabaseMigrator(options, SmartSpare.ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance)
            .RunAsync(TestContext.Current.CancellationToken);

        MigratorConnectionString = options.BuildMigratorConnectionString();
        ApiConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "smartspare",
            Username = "smartspare_api",
            Password = options.Passwords.Api,
        }.ConnectionString;

        // Fleet's and Identity's brand copies as the publish job would fill them (STAGE_7_PLAN 3.2); dated at the epoch,
        // so the events of a test are newer.
        await ExecuteAsync(
            """
            INSERT INTO fleet.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at)
            SELECT id, code, name, is_archived, 'epoch' FROM catalog.brand ON CONFLICT (brand_id) DO NOTHING;
            INSERT INTO iam.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at)
            SELECT id, code, name, is_archived, 'epoch' FROM catalog.brand ON CONFLICT (brand_id) DO NOTHING;
            """);
    }

    /// <summary>
    /// Creates an active user with <see cref="Password"/>, hashed with the given iteration count. The user belongs to an
    /// employee with a position of its own whose role template is <paramref name="roles"/> (ADR 0026); the position's code
    /// is <c>T_</c> and the first 12 hex digits of the id. <paramref name="brandCodes"/> become brand-manager assignments on
    /// the brands and Identity's projection of them, as if <c>ResponsibilitiesChanged</c> had been delivered.
    /// </summary>
    public async Task<Guid> CreateUserAsync(string userName, string[] roles, string[] brandCodes, int iterations = IdentityModuleInstaller.PasswordHashIterations)
    {
        var id = Guid.NewGuid();
        var hasherOptions = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(hasherOptions);
        hasherOptions.IterationCount = iterations;
        var hash = new PasswordHasher<object>(Options.Create(hasherOptions)).HashPassword(new object(), Password);

        await ExecuteAsync(
            """
            INSERT INTO iam.position (code, name) VALUES ('T_' || upper(left(replace(@id::text, '-', ''), 12)), 'Test position');
            INSERT INTO iam.position_role (position_id, role_id)
            SELECT p.id, r.id FROM iam.position p JOIN iam.role r ON r.code = ANY(@roles) WHERE p.code = 'T_' || upper(left(replace(@id::text, '-', ''), 12));
            INSERT INTO iam.employee (id, code, full_name, position_id)
            SELECT @id, 'E' || lpad((coalesce((SELECT max(substr(code, 2)::int) FROM iam.employee), 0) + 1)::text, 6, '0'), 'Test User', p.id
            FROM iam.position p WHERE p.code = 'T_' || upper(left(replace(@id::text, '-', ''), 12));
            INSERT INTO iam.app_user (id, employee_id, user_name, password_hash) VALUES (@id, @id, @name, @hash);
            INSERT INTO catalog.brand_responsible (brand_id, employee_id, record_kind, assignment_role)
            SELECT b.id, @id, 'brand', 'BRAND_MANAGER' FROM catalog.brand b WHERE b.code = ANY(@brands);
            INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
            SELECT 'brand', b.id, @id, 'BRAND_MANAGER' FROM catalog.brand b WHERE b.code = ANY(@brands);
            """,
            ("id", id), ("name", userName), ("hash", hash), ("roles", roles), ("brands", brandCodes));
        return id;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async Task<short[]> BrandIdsAsync(params string[] codes)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT id FROM catalog.brand WHERE code = ANY(@codes) ORDER BY id", connection);
        command.Parameters.AddWithValue("codes", codes);
        var ids = new List<short>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetInt16(0));
        }

        return [.. ids];
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

[CollectionDefinition(Name)]
public sealed class IdentityDatabaseGroup : ICollectionFixture<IdentityDatabaseFixture>
{
    public const string Name = "Host with Identity database";
}
