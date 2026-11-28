using Microsoft.Extensions.Logging;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// Bootstraps the server if needed, then applies every context's migrations in catalogue order, or only the Platform's and
/// one module's with <see cref="MigrationRunnerOptions.Module"/> (ADR 0020). <see cref="RunsAfter"/> lists the few
/// migrations that must wait for another module's migration when both are pending in the same run: module by module, a
/// database several migrations behind would otherwise run a later step of one module before an earlier step of another.
/// The upgrade test from v0.6.0 (tools/upgrade_test.sh) covers it.
/// </summary>
public sealed partial class DatabaseMigrator(
    MigrationRunnerOptions options,
    IReadOnlyList<IModuleDatabase> databases,
    ILogger<DatabaseMigrator> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.AdminConnectionString))
        {
            await BootstrapIfNeededAsync(cancellationToken).ConfigureAwait(false);
        }

        var connectionString = options.BuildMigratorConnectionString();
        var scope = await MigrationScope.SelectAsync(
            databases, options.Module, d => d.GetPendingMigrationsAsync(connectionString, cancellationToken)).ConfigureAwait(false);
        var pending = new Dictionary<IModuleDatabase, List<string>>();
        foreach (var database in scope)
        {
            pending[database] = [.. await database.GetPendingMigrationsAsync(connectionString, cancellationToken).ConfigureAwait(false)];
        }

        foreach (var (database, target, migrations) in Plan(scope, pending))
        {
            await database.MigrateToAsync(connectionString, target, cancellationToken).ConfigureAwait(false);
            foreach (var migration in migrations)
            {
                LogApplied(database.Name, migration);
            }
        }

        foreach (var database in scope.Where(d => pending[d].Count == 0))
        {
            LogUpToDate(database.Name);
        }
    }

    /// <summary>
    /// Migrations that run only after another module's migration, when both are pending in the same run (module name,
    /// migration id → module name, migration id). Stage 7's cross-module data migration (STAGE_7_PLAN 1.6): Identity
    /// drops the legacy access data only after Reporting copied it into the assignments. Kept here, so no applied
    /// migration changes.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Module, string Migration), (string Module, string Migration)> RunsAfter =
        new Dictionary<(string, string), (string, string)>
        {
            [("identity", "20261127163529_DropLegacyAccess")] = ("reporting", "20261127154602_LegacyAccessToAssignments"),
        };

    /// <summary>
    /// The pending migrations as runs of one context, in catalogue order. A context that reaches a migration listed in
    /// <see cref="RunsAfter"/> whose awaited migration is still pending in the run stops there and waits; the others go
    /// on in catalogue order, and as soon as the awaited migration is applied, the waiting context continues, before the
    /// rest of the awaited context. Without a listed migration pending this is exactly the catalogue order. Empties
    /// <paramref name="pending"/> as it plans.
    /// </summary>
    public static IReadOnlyList<(IModuleDatabase Database, string Target, IReadOnlyList<string> Migrations)> Plan(
        IReadOnlyList<IModuleDatabase> scope, Dictionary<IModuleDatabase, List<string>> pending)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(pending);
        var runs = new List<(IModuleDatabase, string, IReadOnlyList<string>)>();
        var waiting = new List<(IModuleDatabase Database, string Module, string Migration)>();

        (string Module, string Migration)? Awaited(IModuleDatabase database, string migration) =>
            RunsAfter.TryGetValue((database.Name, migration), out var awaited)
            && scope.FirstOrDefault(d => d.Name == awaited.Module) is { } other
            && pending[other].Contains(awaited.Migration, StringComparer.Ordinal)
                ? awaited
                : null;

        // Migrates the context as far as it may: up to its end, up to (excluding) a migration that must wait, or up
        // to (including) a migration another context waits for, which then resumes.
        void Run(IModuleDatabase database)
        {
            while (pending[database].Count > 0)
            {
                var list = pending[database];
                var count = 0;
                (string Module, string Migration)? blockedBy = null;
                var resumes = false;
                while (count < list.Count)
                {
                    if (Awaited(database, list[count]) is { } awaited)
                    {
                        blockedBy = awaited;
                        break;
                    }

                    count++;
                    if (waiting.Any(w => w.Module == database.Name && w.Migration == list[count - 1]))
                    {
                        resumes = true;
                        break;
                    }
                }

                if (count > 0)
                {
                    var migrations = list.Take(count).ToList();
                    list.RemoveRange(0, count);
                    runs.Add((database, migrations[^1], migrations));
                }

                if (blockedBy is { } b)
                {
                    waiting.Add((database, b.Module, b.Migration));
                    return;
                }

                if (resumes)
                {
                    var done = runs[^1].Item3[^1];
                    foreach (var w in waiting.Where(w => w.Module == database.Name && w.Migration == done).ToList())
                    {
                        waiting.Remove(w);
                        Run(w.Database);
                    }
                }
            }
        }

        foreach (var database in scope)
        {
            Run(database);
        }

        return runs;
    }

    /// <summary>
    /// Runs the cluster bootstrap unless the database and every login role already exist. The script is idempotent, so a
    /// server bootstrapped before a login was added gets it on the next run (existing passwords stay).
    /// </summary>
    public async Task BootstrapIfNeededAsync(CancellationToken cancellationToken)
    {
        var admin = new NpgsqlConnectionStringBuilder(options.AdminConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var check = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM pg_roles WHERE rolname = ANY(@logins)) = cardinality(@logins) "
            + "AND EXISTS (SELECT 1 FROM pg_database WHERE datname = @db)",
            connection))
        {
            check.Parameters.AddWithValue("db", options.Database);
            check.Parameters.AddWithValue("logins", LoginRoles);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                LogBootstrapSkipped();
                return;
            }
        }

        if (!string.Equals(options.Database, "smartspare", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bootstrap script creates the database 'smartspare'; Migrations:Database must match.");
        }

        LogBootstrapping(admin.Host ?? "?");
        await BootstrapScript.ExecuteAsync(connection, options.Passwords.ToVariables(), cancellationToken).ConfigureAwait(false);
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Login roles the bootstrap creates; a missing one means the bootstrap runs again.</summary>
    public static readonly string[] LoginRoles =
    [
        "smartspare_migrator", "smartspare_api", "smartspare_jobs", "smartspare_reporting",
        "smartspare_identity", "smartspare_inventory", "smartspare_fleet", "smartspare_procurement", "smartspare_forecasting",
    ];

    [LoggerMessage(Level = LogLevel.Critical, Message = "Migration failed.")]
    public partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap skipped: roles and database already exist.")]
    private partial void LogBootstrapSkipped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrapping roles and database on {Host}.")]
    private partial void LogBootstrapping(string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Database}: up to date.")]
    private partial void LogUpToDate(string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Database}: applied {Migration}.")]
    private partial void LogApplied(string database, string migration);
}
