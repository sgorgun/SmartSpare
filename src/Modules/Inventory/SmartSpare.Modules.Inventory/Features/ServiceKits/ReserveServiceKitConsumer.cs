using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.ServiceKits;

/// <summary>
/// Reserves the kit of a drafted service order (STAGE_6_PLAN 5.3, MODULE_MAPPING X6, #12): soft holds for
/// <see cref="HoldDays"/> days at the order's hub, partially if stock is short, then <see cref="ServiceKitReserved"/> to
/// Fleet through the outbox in the same transaction. Runs as the user who drafted the order (<see cref="IActedByUser"/>).
/// A redelivered event is skipped by the inbox; <c>inventory.reserve_service_kit</c> is idempotent per line as well.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ReserveServiceKitConsumer(ServiceKitStore store, IUnitOfWork<InventoryDbContext> unitOfWork, TimeProvider time)
    : IIntegrationEventConsumer<ServiceOrderDrafted>
{
    public const string Name = "inventory.reserve-service-kit";

    /// <summary>How long a soft hold of a service kit lasts (UC02: 7 days).</summary>
    public const int HoldDays = 7;

    public async Task Handle(ServiceOrderDrafted integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var expiresAt = time.GetUtcNow().AddDays(HoldDays);
        var reserved = integrationEvent.Lines.Count == 0
            ? []
            : await store.ReserveAsync(
                    integrationEvent.WarehouseId, [.. integrationEvent.Lines.Select(l => (l.LineId, l.PartId, l.Quantity))], expiresAt, cancellationToken)
                .ConfigureAwait(false);

        unitOfWork.AddIntegrationEvent(KitReservation.Reserved(integrationEvent, reserved, expiresAt));
    }
}

/// <summary>The answer to a drafted order: what is held per line, and whether the whole kit is.</summary>
internal static class KitReservation
{
    public static ServiceKitReserved Reserved(ServiceOrderDrafted drafted, IReadOnlyList<ReservedLine> reserved, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(drafted);
        ArgumentNullException.ThrowIfNull(reserved);
        var held = reserved.ToDictionary(r => r.ServiceOrderLineId, r => r.ReservedQty);
        var lines = drafted.Lines.Select(l => new ReservedKitLine(l.LineId, held.GetValueOrDefault(l.LineId))).ToList();
        var fully = drafted.Lines.Count > 0 && drafted.Lines.All(l => held.GetValueOrDefault(l.LineId) >= l.Quantity);
        return new ServiceKitReserved(drafted.ServiceOrderId, lines, fully, expiresAt, drafted.ActorId);
    }
}
