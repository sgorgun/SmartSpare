using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>The integration events of a service order (STAGE_6_PLAN 5.1).</summary>
internal static class ServiceOrderEvents
{
    public static ServiceOrderDrafted Drafted(ServiceOrder order, Guid actorId, DateTimeOffset draftedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ServiceOrderDrafted(
            order.Id, order.Number, order.WarehouseId,
            [.. order.Lines.OrderBy(l => l.PartId).Select(l => new ServiceOrderKitLine(l.Id, l.PartId, l.QtyOpen))],
            actorId, draftedAt);
    }
}
