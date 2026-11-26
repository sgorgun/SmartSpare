using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>
/// Stores what Inventory holds for a service order's kit (<see cref="ServiceKitReserved"/>, STAGE_6_PLAN 5.3): the
/// per-line <c>qty_reserved</c> copy, and parts reserved when the whole kit is held. The delivery pipeline saves the change
/// with the inbox row; a reservation for an order that is no longer open is ignored (5.5).
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed partial class ApplyKitReservedConsumer(FleetDbContext db, ILogger<ApplyKitReservedConsumer> logger)
    : IIntegrationEventConsumer<ServiceKitReserved>
{
    public const string Name = "fleet.apply-kit-reserved";

    public async Task Handle(ServiceKitReserved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await db.Set<ServiceOrder>().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == integrationEvent.ServiceOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !order.ApplyKitReserved(integrationEvent.Lines.ToDictionary(l => l.LineId, l => l.ReservedQty)))
        {
            LogIgnored(integrationEvent.ServiceOrderId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kit reservation for service order {ServiceOrderId} ignored: the order is missing or no longer open.")]
    private partial void LogIgnored(long serviceOrderId);
}
