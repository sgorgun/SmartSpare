using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.ServiceKits;

/// <summary>
/// Expires service-kit holds after their 7 days (STAGE_6_PLAN 5.5, MODULE_MAPPING X5, #12): one transaction expires the
/// overdue holds at the job's clock (<see cref="TimeProvider"/>, so tests move time instead of editing data) and writes
/// <see cref="ServiceKitReservationExpired"/> with their lines to the outbox. <c>--job inventory.reservation-expiry</c> in
/// the services topology (a Container Apps cron job from stage 10); every 15 minutes from Quartz in the single host (#30).
/// </summary>
internal sealed class ReservationExpiryJob(ServiceKitStore store, EfUnitOfWork<InventoryDbContext> unitOfWork, TimeProvider time) : IJob
{
    public const string JobName = "inventory.reservation-expiry";

    public string Name => JobName;

    public Task RunAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var now = time.GetUtcNow();
                var lines = await store.ExpireAsync(now, ct).ConfigureAwait(false);
                foreach (var expired in KitExpiry.Events(lines, now))
                {
                    unitOfWork.AddIntegrationEvent(expired);
                }

                return lines.Count;
            },
            _ => true,
            cancellationToken);
}

/// <summary>Expired lines as <see cref="ServiceKitReservationExpired"/> events of at most <see cref="ServiceKitReservationExpired.MaxLines"/> lines.</summary>
internal static class KitExpiry
{
    public static IReadOnlyList<ServiceKitReservationExpired> Events(IReadOnlyList<long> lineIds, DateTimeOffset expiredAt)
    {
        ArgumentNullException.ThrowIfNull(lineIds);
        return [.. lineIds.Order().Chunk(ServiceKitReservationExpired.MaxLines).Select(chunk => new ServiceKitReservationExpired(chunk, expiredAt))];
    }
}
