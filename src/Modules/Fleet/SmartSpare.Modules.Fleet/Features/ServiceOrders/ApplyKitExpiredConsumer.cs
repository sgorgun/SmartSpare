using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>
/// Reopens service orders whose kit holds expired (<see cref="ServiceKitReservationExpired"/>, STAGE_6_PLAN 5.5,
/// MODULE_MAPPING X5): the lines' <c>qty_reserved</c> copy goes to 0 and a parts-reserved order returns to draft. Closed
/// orders are left alone. Saved with the inbox row by the delivery pipeline.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ApplyKitExpiredConsumer(FleetDbContext db) : IIntegrationEventConsumer<ServiceKitReservationExpired>
{
    public const string Name = "fleet.apply-kit-expired";

    public async Task Handle(ServiceKitReservationExpired integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var lineIds = integrationEvent.LineIds.ToHashSet();
        var orders = await db.Set<ServiceOrder>().Include(o => o.Lines)
            .Where(o => o.Lines.Any(l => lineIds.Contains(l.Id)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var order in orders)
        {
            order.ApplyKitReservationExpired(lineIds);
        }
    }
}
