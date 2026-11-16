using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Messaging.Outbox;

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

    /// <summary>Connection string of the application login (<c>ConnectionStrings:smartspare</c>).</summary>
    public const string ConnectionStringName = "smartspare";

    /// <summary>
    /// Registers a module's persistence for the application: its DbContext, its unit of work for the
    /// commands of <paramref name="moduleAssembly"/> (MODULE_MAPPING 5.6) and, when the context maps
    /// an outbox, the outbox store the dispatcher drains. The application connects with an application
    /// login (never the owner or a superuser) and never migrates on start-up. A <see langword="null"/>
    /// connection string is allowed (host tests without a database); the context then fails on first use.
    /// </summary>
    public static IServiceCollection AddModulePersistence<TContext>(
        this IServiceCollection services,
        Assembly moduleAssembly,
        string? connectionString,
        string historySchema,
        Action<NpgsqlDbContextOptionsBuilder>? configureNpgsql = null,
        string? outboxSchema = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(moduleAssembly);

        services.TryAddScoped<ICurrentUser, NoCurrentUser>();
        services.TryAddScoped<SessionContextInterceptor>();
        services.AddDbContext<TContext>((sp, options) =>
        {
            Configure(options, connectionString, historySchema, npgsql =>
            {
                npgsql.EnableRetryOnFailure(3);              // transient Azure network errors
                configureNpgsql?.Invoke(npgsql);
            });
            options.AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>());
        });

        services.AddScoped<EfUnitOfWork<TContext>>();
        services.AddScoped<IUnitOfWork<TContext>>(sp => sp.GetRequiredService<EfUnitOfWork<TContext>>());
        services.AddUnitOfWork<EfUnitOfWork<TContext>>(moduleAssembly);

        if (outboxSchema is not null)
        {
            services.AddSingleton<IOutboxStore>(new EfOutboxStore<TContext>(outboxSchema));
        }

        return services;
    }

    /// <summary>
    /// Declares <typeparamref name="TUnitOfWork"/> as the unit of work of the commands in
    /// <paramref name="moduleAssembly"/>. <c>DecorateHandlers</c> fails at start-up if a command's
    /// assembly has none.
    /// </summary>
    public static IServiceCollection AddUnitOfWork<TUnitOfWork>(this IServiceCollection services, Assembly moduleAssembly)
        where TUnitOfWork : class, ITransactionalUnitOfWork
    {
        ArgumentNullException.ThrowIfNull(moduleAssembly);
        services.TryAddScoped<TUnitOfWork>();
        services.AddSingleton(new UnitOfWorkRegistration(moduleAssembly, typeof(TUnitOfWork)));
        services.TryAddSingleton<UnitOfWorkRegistry>();
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
