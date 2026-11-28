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

    /// <summary>The completed order with what is still to be issued per line.</summary>
    public static ServiceOrderCompleted Completed(ServiceOrder order, Guid actorId, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ServiceOrderCompleted(
            order.Id, order.Number, order.WarehouseId,
            [.. order.Lines.Where(l => l.QtyOpen > 0).OrderBy(l => l.PartId).Select(l => new ServiceOrderKitLine(l.Id, l.PartId, l.QtyOpen))],
            actorId, completedAt);
    }

    /// <summary>The cancelled order with all its lines, whose holds Inventory releases.</summary>
    public static ServiceOrderCancelled Cancelled(ServiceOrder order, string reason, Guid actorId, DateTimeOffset cancelledAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new ServiceOrderCancelled(order.Id, order.Number, [.. order.Lines.Select(l => l.Id).Order()], reason, actorId, cancelledAt);
    }


    /// <summary>
    /// The hubs the orders hold, open or closed, for Inventory's projection of Fleet's hub references (ADR 0029), which the
    /// warehouse archive guard reads. Written with every draft, completion and cancellation.
    /// </summary>
    public static HubReference[] HubReferences(IEnumerable<ServiceOrder> orders, bool open) =>
        [.. orders.Select(o => new HubReference(HubReference.ServiceOrder, o.Id, o.WarehouseId, open))];

    /// <summary>The edited kit: removed line ids and the new lines with the ids they got when saved.</summary>
    public static ServiceOrderLinesChanged LinesChanged(ServiceOrder order, KitLineChange change, Guid actorId, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(change);
        return new ServiceOrderLinesChanged(
            order.Id,
            order.WarehouseId,
            [.. change.AddedLines.OrderBy(l => l.PartId).Select(l => new ServiceOrderKitLine(l.Id, l.PartId, l.QtyRequired))],
            [.. change.RemovedLineIds.Order()],
            actorId,
            changedAt);
    }
}
