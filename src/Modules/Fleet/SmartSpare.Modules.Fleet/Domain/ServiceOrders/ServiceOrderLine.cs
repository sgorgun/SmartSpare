using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain.ServiceOrders;

/// <summary>Input for a service order line: a part and the quantity the work needs.</summary>
internal sealed record ServiceOrderLineDraft(long PartId, decimal Quantity);

/// <summary>
/// A part needed by a service order (<c>fleet.service_order_line</c>): copied from the plan's kit or entered for a
/// repair. <see cref="QtyIssued"/> follows the parts actually issued from stock (<c>ck_service_order_line_issued</c>:
/// never above the required quantity).
/// </summary>
internal sealed class ServiceOrderLine : Entity<long>
{
    /// <summary>Upper bound of <c>numeric(12,3)</c>.</summary>
    public const decimal MaxQuantity = 999_999_999.999m;

    private ServiceOrderLine()
        : base(0)
    {
    }

    private ServiceOrderLine(long partId, decimal qtyRequired)
        : base(0)
    {
        PartId = partId;
        QtyRequired = qtyRequired;
    }

    public long ServiceOrderId { get; private set; }

    /// <summary>Plain reference to an Inventory part (no cross-module FK, MODULE_MAPPING 2.3).</summary>
    public long PartId { get; private set; }

    public decimal QtyRequired { get; private set; }

    public decimal QtyIssued { get; private set; }

    /// <summary>
    /// Fleet's copy of the quantity Inventory holds for this line (MODULE_MAPPING 3.5): written by the transitional draft
    /// until PR 6g2, then from <c>ServiceKitReserved</c>. Never above the required quantity
    /// (<c>ck_service_order_line_reserved</c>).
    /// </summary>
    public decimal QtyReserved { get; private set; }

    /// <summary>What the kit still lacks: required minus issued minus reserved.</summary>
    public decimal QtyMissing => Math.Max(0, QtyRequired - QtyIssued - QtyReserved);

    /// <summary>What is still to be issued.</summary>
    public decimal QtyOpen => QtyRequired - QtyIssued;

    public static Result<ServiceOrderLine> Create(ServiceOrderLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return IsValidQuantity(draft.Quantity)
            ? new ServiceOrderLine(draft.PartId, draft.Quantity)
            : FleetErrors.InvalidQuantity;
    }

    internal Result ChangeQuantity(decimal quantity)
    {
        if (!IsValidQuantity(quantity) || quantity < QtyIssued)
        {
            return FleetErrors.InvalidQuantity;
        }

        QtyRequired = quantity;
        return Result.Success();
    }

    /// <summary>Positive, at most three decimals, within <c>numeric(12,3)</c>.</summary>
    public static bool IsValidQuantity(decimal quantity) =>
        quantity is > 0 and <= MaxQuantity && decimal.Round(quantity, 3) == quantity;
}
