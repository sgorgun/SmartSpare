using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>
/// Stores what Inventory issued for a completed order (<see cref="ServiceKitIssued"/>, STAGE_6_PLAN 5.4): the per-line
/// <c>qty_issued</c>; the rest of the required quantity is the shortfall the order shows (F7). Saved with the inbox row.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed partial class ApplyKitIssuedConsumer(FleetDbContext db, ILogger<ApplyKitIssuedConsumer> logger)
    : IIntegrationEventConsumer<ServiceKitIssued>
{
    public const string Name = "fleet.apply-kit-issued";

    public async Task Handle(ServiceKitIssued integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await db.Set<ServiceOrder>().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == integrationEvent.ServiceOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !order.ApplyKitIssued(integrationEvent.Lines.ToDictionary(l => l.LineId, l => l.IssuedQty)))
        {
            LogIgnored(integrationEvent.ServiceOrderId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kit issue for service order {ServiceOrderId} ignored: the order is missing or not completed.")]
    private partial void LogIgnored(long serviceOrderId);
}
