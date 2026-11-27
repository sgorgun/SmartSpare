using SmartSpare.ApiContracts.Fleet;

namespace SmartSpare.Web.Client.Features.Fleet;

/// <summary>
/// When the order detail asks the server again on its own (STAGE_6_PLAN section 6): Inventory answers a draft or a
/// completion through events, a moment later. The page refreshes while the kit is being reserved, and for a short while
/// after a completion until Inventory reports what it issued. It never polls for ever: a reservation that never arrives
/// (an event lost or not sent) leaves the "Reserving parts…" state with a hint to reload.
/// </summary>
public static class KitRefresh
{
    /// <summary>Time between two refreshes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>How long the page waits for Inventory's answer before it stops asking.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Whether Inventory's answer for this order is still on its way.</summary>
    public static bool Pending(ServiceOrderDetails order, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Order.KitStatus == KitStatuses.Reserving)
        {
            return true;
        }

        // Completed a moment ago and nothing issued yet: ServiceKitIssued has not arrived.
        return order.Order.Status == ServiceOrderStatuses.Completed
            && order.Order.CompletedAt is { } completedAt
            && now - completedAt < Patience
            && order.Lines.Count > 0
            && order.Lines.All(l => l.QtyIssued == 0);
    }
}
