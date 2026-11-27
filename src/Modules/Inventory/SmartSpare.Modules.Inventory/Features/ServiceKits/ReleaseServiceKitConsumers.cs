using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.ServiceKits;

/// <summary>
/// Releases the holds of a cancelled service order (<see cref="ServiceOrderCancelled"/>, STAGE_6_PLAN 5.5, MODULE_MAPPING
/// X8, #12), as the user who cancelled (ADR 0024). Idempotent: a line without an open hold changes nothing, so a cancellation
/// that overtakes the reservation leaves nothing to release, and the late reservation is released on
/// <see cref="ServiceKitReleaseRequested"/>.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ReleaseCancelledKitConsumer(ServiceKitStore store) : IIntegrationEventConsumer<ServiceOrderCancelled>
{
    public const string Name = "inventory.release-cancelled-kit";

    public async Task Handle(ServiceOrderCancelled integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.LineIds.Count > 0)
        {
            await store.ReleaseAsync(integrationEvent.LineIds, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Releases holds that were placed for an order Fleet had already closed (<see cref="ServiceKitReleaseRequested"/>,
/// STAGE_6_PLAN 5.5): a reservation that arrived after the completion or the cancellation.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ReleaseLateKitConsumer(ServiceKitStore store) : IIntegrationEventConsumer<ServiceKitReleaseRequested>
{
    public const string Name = "inventory.release-late-kit";

    public async Task Handle(ServiceKitReleaseRequested integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.LineIds.Count > 0)
        {
            await store.ReleaseAsync(integrationEvent.LineIds, cancellationToken).ConfigureAwait(false);
        }
    }
}
