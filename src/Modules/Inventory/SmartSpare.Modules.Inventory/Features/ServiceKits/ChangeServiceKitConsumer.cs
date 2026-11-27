using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.ServiceKits;

/// <summary>
/// Follows an edited kit (<see cref="ServiceOrderLinesChanged"/>, PR 6i, #140), as the user who edited it (ADR 0024): in one
/// transaction releases the holds of the removed lines and reserves the added lines for <see cref="ReserveServiceKitConsumer.HoldDays"/>
/// days, answering <see cref="ServiceKitReserved"/> for the added lines. Both SQL functions are idempotent per line, and a
/// redelivered event is skipped by the inbox. If edits overtake each other, a hold of a line that no longer exists simply
/// expires after its 7 days and Fleet ignores the unknown line.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ChangeServiceKitConsumer(ServiceKitStore store, IUnitOfWork<InventoryDbContext> unitOfWork, TimeProvider time)
    : IIntegrationEventConsumer<ServiceOrderLinesChanged>
{
    public const string Name = "inventory.change-service-kit";

    public async Task Handle(ServiceOrderLinesChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.RemovedLineIds.Count > 0)
        {
            await store.ReleaseAsync(integrationEvent.RemovedLineIds, cancellationToken).ConfigureAwait(false);
        }

        if (integrationEvent.AddedLines.Count == 0)
        {
            return;
        }

        var expiresAt = time.GetUtcNow().AddDays(ReserveServiceKitConsumer.HoldDays);
        var reserved = await store.ReserveAsync(
                integrationEvent.WarehouseId, [.. integrationEvent.AddedLines.Select(l => (l.LineId, l.PartId, l.Quantity))], expiresAt, cancellationToken)
            .ConfigureAwait(false);
        var held = reserved.ToDictionary(r => r.ServiceOrderLineId, r => r.ReservedQty);
        unitOfWork.AddIntegrationEvent(new ServiceKitReserved(
            integrationEvent.ServiceOrderId,
            [.. integrationEvent.AddedLines.Select(l => new ReservedKitLine(l.LineId, held.GetValueOrDefault(l.LineId)))],
            integrationEvent.AddedLines.All(l => held.GetValueOrDefault(l.LineId) >= l.Quantity),
            expiresAt,
            integrationEvent.ActorId));
    }
}
