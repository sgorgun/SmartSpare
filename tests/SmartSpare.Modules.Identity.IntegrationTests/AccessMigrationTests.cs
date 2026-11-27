using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSpare.MigrationRunner;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// Migration step 1 of STAGE_7_PLAN 1.6 on a database that already has users: Identity is migrated to the last stage 6
/// migration, users with every kind of role set are written the old way (iam.user_role), and the migration Organisation
/// then turns each one into an employee whose position's template equals the old roles, so no account gains or loses
/// a role. The data step skips users that already have an employee, so running it again changes nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AccessMigrationTests : IAsyncLifetime
{
    private const string LastStage6Migration = "20261127033447_MessageRetention";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private string _migrator = null!;
    private MigrationRunnerOptions _options = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(Ct);
        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _container.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = NewPassword(), Api = NewPassword(), Jobs = NewPassword(), Reporting = NewPassword() },
        };
        var platform = ModuleCatalog.ModuleCatalog.Databases().Where(d => d.Name == "platform").ToList();
        await new DatabaseMigrator(options, platform, NullLogger<DatabaseMigrator>.Instance).RunAsync(Ct);
        _migrator = options.BuildMigratorConnectionString();
        _options = options;

        await using var context = IdentityDatabase.Instance.CreateContext(_migrator);
        await context.GetService<IMigrator>().MigrateAsync(LastStage6Migration, Ct);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Every_existing_user_becomes_an_employee_with_exactly_the_old_roles()
    {
        await ExecuteAsync(
            """
            INSERT INTO iam.app_user (id, user_name, email, full_name, phone, password_hash, home_warehouse_id, is_active) VALUES
             ('00000000-0000-4000-8000-0000000000a1', 'legacy.manager',  'm@dealer.example', 'Legacy Manager', '+370 600 00001', 'AQAAAAIAA1tgAAAAE' || repeat('A', 67), NULL, true),
             ('00000000-0000-4000-8000-0000000000a2', 'legacy.director', 'd@dealer.example', 'Legacy Director', NULL,           'AQAAAAIAA1tgAAAAE' || repeat('A', 67), NULL, true),
             ('00000000-0000-4000-8000-0000000000a3', 'legacy.odd',      'o@dealer.example', 'Legacy Odd',      NULL,           'AQAAAAIAA1tgAAAAE' || repeat('A', 67), 3,    true),
             ('00000000-0000-4000-8000-0000000000a4', 'legacy.none',     'n@dealer.example', 'Legacy None',     NULL,           'AQAAAAIAA1tgAAAAE' || repeat('A', 67), NULL, true),
             ('00000000-0000-4000-8000-0000000000a5', 'legacy.off',      'f@dealer.example', 'Legacy Off',      NULL,           NULL,                                  NULL, false),
             ('00000000-0000-4000-8000-0000000000a6', 'legacy.keeper',   'k@dealer.example', 'Legacy Keeper',   NULL,           'AQAAAAIAA1tgAAAAE' || repeat('A', 67), 3,    true),
             ('00000000-0000-4000-8000-0000000000a7', 'legacy.engineer', 'e@dealer.example', 'Legacy Engineer', NULL,           'AQAAAAIAA1tgAAAAE' || repeat('A', 67), 4,    true);
            INSERT INTO iam.user_role (user_id, role_id)
            SELECT ('00000000-0000-4000-8000-0000000000' || x.u)::uuid, r.id
            FROM (VALUES ('a1', 'BRAND_MANAGER'), ('a2', 'BRAND_MANAGER'), ('a2', 'SPONSOR'), ('a3', 'WAREHOUSE_KEEPER'), ('a3', 'SERVICE_ENGINEER'),
                         ('a5', 'SERVICE_ENGINEER'), ('a6', 'WAREHOUSE_KEEPER'), ('a7', 'SERVICE_ENGINEER')) AS x (u, role)
            JOIN iam.role r ON r.code = x.role;
            INSERT INTO iam.user_brand (user_id, brand_id)
            SELECT u.id, b.id FROM iam.app_user u, ref.brand b WHERE u.user_name IN ('legacy.manager', 'legacy.director') AND b.code IN ('CLAAS', 'JD');
            UPDATE iam.app_user SET is_active = false, password_hash = NULL, anonymised_at = now(),
                   user_name = 'anon_a5', email = 'anon-a5@anonymised.invalid', full_name = 'Anonymised User'
             WHERE user_name = 'legacy.off';
            """);
        var before = await RolesAsync(fromPositions: false);

        // Every module, as the migration runner applies them: Identity's step 1, Inventory's assignment tables, and the
        // Reporting copy of step 2, which runs last.
        await new DatabaseMigrator(_options, ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(Ct);

        var after = await RolesAsync(fromPositions: true);
        after.ShouldBe(before, "every account keeps exactly its roles");
        (await ScalarAsync<long>("SELECT count(*) FROM iam.app_user WHERE employee_id IS NULL")).ShouldBe(0);
        (await ScalarAsync<string>("SELECT p.code FROM iam.employee e JOIN iam.position p ON p.id = e.position_id WHERE e.id = '00000000-0000-4000-8000-0000000000a2'"))
            .ShouldBe("COMMERCIAL_DIRECTOR", "a role set equal to a standard template takes that position");
        (await ScalarAsync<string>("SELECT p.name FROM iam.employee e JOIN iam.position p ON p.id = e.position_id WHERE e.id = '00000000-0000-4000-8000-0000000000a4'"))
            .ShouldBe("Staff without system roles");
        (await ScalarAsync<bool>("SELECT p.code LIKE 'POS\\_%' FROM iam.employee e JOIN iam.position p ON p.id = e.position_id WHERE e.id = '00000000-0000-4000-8000-0000000000a3'"))
            .ShouldBeTrue("an unusual role set gets a position of its own");
        (await ScalarAsync<bool>(
            "SELECT is_archived AND anonymised_at IS NOT NULL AND email IS NULL AND full_name = 'Anonymised Employee' FROM iam.employee WHERE id = '00000000-0000-4000-8000-0000000000a5'"))
            .ShouldBeTrue("an anonymised user becomes an anonymised employee who left");
        (await ScalarAsync<string>("SELECT full_name || ' ' || email || ' ' || phone FROM iam.employee WHERE id = '00000000-0000-4000-8000-0000000000a1'"))
            .ShouldBe("Legacy Manager m@dealer.example +370 600 00001");
        (await ScalarAsync<long>("SELECT count(DISTINCT code) FROM iam.employee")).ShouldBe(7);

        // Step 2: brand lists become brand-manager assignments, home hubs keeper (keepers) or service-base assignments,
        // and Identity's projection holds the rights-bearing ones; the anonymised user is not copied.
        (await ScalarAsync<long>("SELECT count(*) FROM catalog.brand_responsible WHERE assignment_role = 'BRAND_MANAGER'")).ShouldBe(4);
        (await ScalarAsync<string>(
                "SELECT string_agg(e.code || ':' || r.assignment_role, ',' ORDER BY e.code) FROM inventory.warehouse_responsible r JOIN iam.employee e ON e.id = r.employee_id"))
            .ShouldBe(await ScalarAsync<string>(
                "SELECT string_agg(code || ':' || CASE WHEN id IN ('00000000-0000-4000-8000-0000000000a3', '00000000-0000-4000-8000-0000000000a6') THEN 'KEEPER' ELSE 'SERVICE_BASE' END, ',' ORDER BY code) FROM iam.employee WHERE id IN ('00000000-0000-4000-8000-0000000000a3', '00000000-0000-4000-8000-0000000000a6', '00000000-0000-4000-8000-0000000000a7')"));
        var projection = await ScalarAsync<long>("SELECT count(*) FROM iam.employee_responsibility");
        projection.ShouldBe(7, "4 brand managers and 3 hubs");

        var copy = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "db", "sql", "reporting", "0005_legacy_access_copy.sql"), Ct);
        await ExecuteAsync(copy);
        (await ScalarAsync<long>("SELECT count(*) FROM iam.employee_responsibility")).ShouldBe(projection, "the copy is idempotent");
        (await ScalarAsync<long>("SELECT count(*) FROM catalog.brand_responsible")).ShouldBe(4);

        var employees = await ScalarAsync<long>("SELECT count(*) FROM iam.employee");
        var script = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "db", "sql", "identity", "0007_organisation.sql"), Ct);
        await ExecuteAsync(DataStep(script));
        (await ScalarAsync<long>("SELECT count(*) FROM iam.employee")).ShouldBe(employees, "a second run finds every user already an employee");
    }

    /// <summary>Sections 1 and 2 of the script (seed and data step), which are written to run more than once.</summary>
    private static string DataStep(string script) => script[..script.IndexOf("-- ---- 3. Triggers", StringComparison.Ordinal)];

    /// <summary>user name → sorted role codes, from the old user_role rows or from the employee's position.</summary>
    private async Task<Dictionary<string, string>> RolesAsync(bool fromPositions)
    {
        var sql = fromPositions
            ? """
              SELECT u.user_name::text, coalesce(string_agg(r.code, ',' ORDER BY r.code), '')
              FROM iam.app_user u JOIN iam.employee e ON e.id = u.employee_id
              LEFT JOIN iam.position_role pr ON pr.position_id = e.position_id LEFT JOIN iam.role r ON r.id = pr.role_id
              WHERE u.anonymised_at IS NULL GROUP BY u.user_name
              """
            : """
              SELECT u.user_name::text, coalesce(string_agg(r.code, ',' ORDER BY r.code), '')
              FROM iam.app_user u LEFT JOIN iam.user_role ur ON ur.user_id = u.id LEFT JOIN iam.role r ON r.id = ur.role_id
              WHERE u.anonymised_at IS NULL GROUP BY u.user_name
              """;
        await using var connection = new NpgsqlConnection(_migrator);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            roles[reader.GetString(0)] = reader.GetString(1);
        }

        return roles;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_migrator);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_migrator);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

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

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
