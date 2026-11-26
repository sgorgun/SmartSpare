using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Transfers;

/// <summary>
/// A part and quantity of a <see cref="TransferOrder"/>. <see cref="ProposedQty"/> keeps the system proposal for
/// KPI-04; a different quantity needs <see cref="OverrideReason"/> (<c>ck_transfer_order_line_override</c>).
/// </summary>
internal sealed class TransferOrderLine : Entity<long>
{
    private TransferOrderLine()
        : base(0)
    {
    }

    private TransferOrderLine(long partId, decimal quantity, decimal? proposedQty, string? overrideReason)
        : base(0)
    {
        PartId = partId;
        Quantity = quantity;
        ProposedQty = proposedQty;
        OverrideReason = overrideReason;
    }

    /// <summary>Set by EF Core from the owning order.</summary>
    public long TransferOrderId { get; private set; }

    public long PartId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? ProposedQty { get; private set; }

    public string? OverrideReason { get; private set; }

    public static Result<TransferOrderLine> Create(TransferLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.PartId <= 0 || Domain.Quantity.Create(draft.Quantity).IsFailure)
        {
            return InventoryErrors.InvalidQuantity;
        }

        if (draft.ProposedQty is { } proposed && Domain.Quantity.Create(proposed).IsFailure)
        {
            return InventoryErrors.InvalidQuantity;
        }

        var reason = Normalise(draft.OverrideReason);
        return NeedsReason(draft.Quantity, draft.ProposedQty) && !IsValidReason(reason)
            ? InventoryErrors.OverrideReasonRequired
            : new TransferOrderLine(draft.PartId, draft.Quantity, draft.ProposedQty, reason);
    }

    internal Result ChangeQuantity(decimal quantity, string? overrideReason)
    {
        if (Domain.Quantity.Create(quantity).IsFailure)
        {
            return InventoryErrors.InvalidQuantity;
        }

        var reason = Normalise(overrideReason) ?? OverrideReason;
        if (NeedsReason(quantity, ProposedQty) && !IsValidReason(reason))
        {
            return InventoryErrors.OverrideReasonRequired;
        }

        Quantity = quantity;
        OverrideReason = reason;
        return Result.Success();
    }

    private static bool NeedsReason(decimal quantity, decimal? proposed) => proposed is { } p && p != quantity;

    private static bool IsValidReason(string? reason) => reason is { Length: > 0 and <= TransferOrder.MaxReasonLength };

    private static string? Normalise(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}
