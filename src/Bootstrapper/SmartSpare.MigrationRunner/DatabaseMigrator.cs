using Microsoft.Extensions.Logging;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// Bootstraps the server if needed, then applies every context's migrations in catalogue order, or only the Platform's and
/// one module's with <see cref="MigrationRunnerOptions.Module"/> (ADR 0020).
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
        foreach (var database in scope)
        {
            var applied = await database.MigrateAsync(connectionString, cancellationToken).ConfigureAwait(false);
            if (applied.Count == 0)
            {
                LogUpToDate(database.Name);
            }

            foreach (var migration in applied)
            {
                LogApplied(database.Name, migration);
            }
        }
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
