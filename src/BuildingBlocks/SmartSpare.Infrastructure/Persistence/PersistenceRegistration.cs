using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SmartSpare.Infrastructure.Persistence;

// C# mirrors of the PostgreSQL ENUM types (names are translated to snake_case by Npgsql).
public enum TransferStatus { Proposed, Approved, Dispatched, Received, Cancelled }
public enum ReservationStatus { Soft, Firm, Consumed, Released, Expired }
public enum ServiceOrderStatus { Draft, PartsReserved, InProgress, Completed, Cancelled }
public enum PoStatus { Draft, PendingApproval, Approved, Exported, PartiallyReceived, Received, Cancelled }

public static class PersistenceRegistration
{
    /// <summary>
    /// Database-first: the schema is owned by db/migrations (SQL), EF Core only maps it.
    /// The API connects as smartspare_api (role app_write) — never as owner or superuser.
    /// SmartSpareDbContext and the entity classes are scaffolded with tools/scaffold-efcore.ps1.
    /// </summary>
    public static IServiceCollection AddSmartSparePersistence<TContext>(this IServiceCollection services, string connectionString)
        where TContext : DbContext
    {
        services.AddScoped<SessionContextInterceptor>();
        services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MapEnum<TransferStatus>("transfer_status", "inventory")
                .MapEnum<ReservationStatus>("reservation_status", "inventory")
                .MapEnum<ServiceOrderStatus>("service_order_status", "fleet")
                .MapEnum<PoStatus>("po_status", "procurement")
                .EnableRetryOnFailure(3))                    // transient Azure network errors
            .UseSnakeCaseNamingConvention()                   // package EFCore.NamingConventions
            .AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>()));
        return services;
    }
}

/*  Optimistic concurrency (UC01 E1, risk R5) — in each scaffolded entity configuration:
 *      builder.Property<uint>("xmin").IsRowVersion();     // PostgreSQL system column, no schema change
 *  DbUpdateConcurrencyException -> HTTP 409 -> the UI re-runs the rebalancing proposal.
 *  Stock itself is protected by pessimistic row locks inside the database functions.
 *
 *  Calling business functions (one transaction, user context set by the interceptor):
 *      await db.Database.ExecuteSqlInterpolatedAsync($"SELECT inventory.approve_transfer({id})");
 *      var orders = await db.Database.SqlQuery<DraftedOrder>(
 *          $"SELECT * FROM fleet.log_engine_hours({machineId}, {hours}, {readAt}, 'manual'::fleet.reading_source)").ToListAsync();
 */
