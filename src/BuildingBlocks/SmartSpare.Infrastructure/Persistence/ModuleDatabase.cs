using Microsoft.EntityFrameworkCore;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// The migrations of one DbContext, as seen by the migration runner. The runner applies them in
/// the order of the module catalogue (Platform first, Reporting last; MODULE_MAPPING section 5.2).
/// Module contexts stay <see langword="internal"/>; a module exposes only this object.
/// </summary>
public interface IModuleDatabase
{
    /// <summary>Lower-case name used in logs, e.g. <c>platform</c>.</summary>
    string Name { get; }

    /// <summary>Schema of the context's <c>__ef_migrations_history</c> table.</summary>
    string HistorySchema { get; }

    Task<IReadOnlyList<string>> GetPendingMigrationsAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Applies all pending migrations; returns their ids in the order applied.</summary>
    Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>True if the model differs from the last migration snapshot (a migration is missing).</summary>
    bool HasPendingModelChanges();
}

/// <summary>Standard <see cref="IModuleDatabase"/> over an EF Core context.</summary>
public abstract class ModuleDatabase<TContext> : IModuleDatabase
    where TContext : DbContext
{
    public abstract string Name { get; }

    public abstract string HistorySchema { get; }

    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        return [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }

    public bool HasPendingModelChanges()
    {
        using var context = CreateContext(connectionString: null);
        return context.Database.HasPendingModelChanges();
    }

    /// <summary>Creates the context with the shared provider settings (<see cref="PersistenceRegistration.Configure"/>).</summary>
    public TContext CreateContext(string? connectionString)
    {
        var options = new DbContextOptionsBuilder<TContext>();
        PersistenceRegistration.Configure(options, connectionString, HistorySchema, ConfigureNpgsql);
        return CreateContext(options.Options);
    }

    protected abstract TContext CreateContext(DbContextOptions<TContext> options);

    /// <summary>Module-specific provider settings, e.g. enum mappings.</summary>
    protected virtual void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
    {
    }
}
