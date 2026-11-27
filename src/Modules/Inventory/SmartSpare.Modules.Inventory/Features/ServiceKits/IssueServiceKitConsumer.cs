using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.ServiceKits;

/// <summary>
/// Issues the kit of a completed service order (STAGE_6_PLAN 5.4, F7, MODULE_MAPPING X7, #12): consumes the holds and
/// posts ISSUE_SERVICE for the open quantities, the unreserved rest up to the available stock, then
/// <see cref="ServiceKitIssued"/> with issued and shortfall per line to Fleet through the outbox. Runs as the user who
/// completed the order (ADR 0024). Redelivery is skipped by the inbox; <c>inventory.issue_service_kit</c> is idempotent per
/// line as well and releases a late hold of a line it issued before.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class IssueServiceKitConsumer(ServiceKitStore store, IUnitOfWork<InventoryDbContext> unitOfWork)
    : IIntegrationEventConsumer<ServiceOrderCompleted>
{
    public const string Name = "inventory.issue-service-kit";

    public async Task Handle(ServiceOrderCompleted integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var issued = integrationEvent.Lines.Count == 0
            ? []
            : await store.IssueAsync(
                    integrationEvent.WarehouseId, [.. integrationEvent.Lines.Select(l => (l.LineId, l.PartId, l.Quantity))], cancellationToken)
                .ConfigureAwait(false);

        unitOfWork.AddIntegrationEvent(new ServiceKitIssued(
            integrationEvent.ServiceOrderId,
            [.. issued.Select(i => new IssuedKitLine(i.ServiceOrderLineId, i.IssuedQty, i.ShortfallQty))],
            integrationEvent.ActorId));
    }
}
