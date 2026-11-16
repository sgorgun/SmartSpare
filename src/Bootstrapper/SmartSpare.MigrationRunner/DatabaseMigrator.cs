using Microsoft.Extensions.Logging;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.MigrationRunner;

/// <summary>Bootstraps the server if needed, then applies every context's migrations in catalogue order.</summary>
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
        foreach (var database in databases)
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

    /// <summary>Runs the cluster bootstrap unless the migrator login and the database already exist.</summary>
    public async Task BootstrapIfNeededAsync(CancellationToken cancellationToken)
    {
        var admin = new NpgsqlConnectionStringBuilder(options.AdminConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_migrator') "
            + "AND EXISTS (SELECT 1 FROM pg_database WHERE datname = @db)",
            connection))
        {
            check.Parameters.AddWithValue("db", options.Database);
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
