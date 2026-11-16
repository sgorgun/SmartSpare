using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Shared EF Core configuration of every module DbContext (ADR 0004, ADR 0011): code-first,
/// snake_case names, one migrations history table per context in the module's own schema.
/// Each module registers its own enums on the Npgsql options it passes in <paramref name="configureNpgsql"/>.
/// </summary>
public static class PersistenceRegistration
{
    /// <summary>Name of the migrations history table; it lives in the owning module's schema.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Registers a module DbContext for the application. The application connects with an
    /// application login (never the owner or a superuser) and never migrates on start-up.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        string historySchema,
        Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null)
        where TContext : DbContext
    {
        services.AddScoped<SessionContextInterceptor>();
        services.AddDbContext<TContext>((sp, options) =>
        {
            Configure(options, connectionString, historySchema, npgsql =>
            {
                npgsql.EnableRetryOnFailure(3);              // transient Azure network errors
                configureNpgsql?.Invoke(npgsql);
            });
            options.AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>());
        });
        return services;
    }

    /// <summary>
    /// Provider settings shared by runtime, design time and the migration runner.
    /// A <see langword="null"/> connection string builds the model only (pending-model-change checks).
    /// </summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        string? connectionString,
        string historySchema,
        Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(historySchema);

        void Npgsql(NpgsqlDbContextOptionsBuilder npgsql)
        {
            npgsql.MigrationsHistoryTable(MigrationsHistoryTable, historySchema);
            configureNpgsql?.Invoke(npgsql);
        }

        if (connectionString is null)
        {
            options.UseNpgsql(Npgsql);
        }
        else
        {
            options.UseNpgsql(connectionString, Npgsql);
        }

        return options.UseSnakeCaseNamingConvention();
    }
}

/*  Optimistic concurrency (UC01 E1, risk R5) — in the entity configuration of an aggregate:
 *      builder.Property<uint>("xmin").IsRowVersion();     // PostgreSQL system column, no schema change
 *  DbUpdateConcurrencyException -> DatabaseErrorMapper -> 409, and the UI re-runs the proposal.
 *  Stock itself is protected by pessimistic row locks inside the database functions.
 */
