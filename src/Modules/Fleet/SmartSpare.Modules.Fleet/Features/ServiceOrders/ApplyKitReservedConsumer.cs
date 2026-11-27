using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>
/// Stores what Inventory holds for a service order's kit (<see cref="ServiceKitReserved"/>, STAGE_6_PLAN 5.3): the
/// per-line <c>qty_reserved</c> copy, and parts reserved when the whole kit is held. The delivery pipeline saves the change
/// with the inbox row. A reservation that reaches an order already completed or cancelled is released at once: the
/// consumer writes <see cref="ServiceKitReleaseRequested"/> for the held lines (5.5, PR 6h3).
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed partial class ApplyKitReservedConsumer(FleetDbContext db, IUnitOfWork<FleetDbContext> unitOfWork, ILogger<ApplyKitReservedConsumer> logger)
    : IIntegrationEventConsumer<ServiceKitReserved>
{
    public const string Name = "fleet.apply-kit-reserved";

    public async Task Handle(ServiceKitReserved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await db.Set<ServiceOrder>().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == integrationEvent.ServiceOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || order.ApplyKitReserved(integrationEvent.Lines.ToDictionary(l => l.LineId, l => l.ReservedQty), integrationEvent.OccurredAt))
        {
            return;
        }

        // The order was closed before Inventory's answer arrived: hand the holds back at once.
        var held = integrationEvent.Lines.Where(l => l.ReservedQty > 0).Select(l => l.LineId).ToList();
        if (held.Count > 0)
        {
            unitOfWork.AddIntegrationEvent(new ServiceKitReleaseRequested(order.Id, held, integrationEvent.ActorId));
            LogReleased(order.Id, held.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kit reservation for closed service order {ServiceOrderId}: release of {Lines} held lines requested.")]
    private partial void LogReleased(long serviceOrderId, int lines);
}
