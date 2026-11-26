namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>How the stock of a part in a hub compares with its stocking policy.</summary>
internal enum StockStatus
{
    /// <summary>Nothing available: everything on hand is reserved, or nothing is on hand.</summary>
    Out,

    /// <summary>Available stock is below the safety stock.</summary>
    BelowSafety,

    /// <summary>Between the safety stock and the maximum.</summary>
    Ok,

    /// <summary>On-hand stock is above the maximum stock.</summary>
    AboveMax,
}

/// <summary>
/// Stock position of a part in a hub: the balance, the stocking policy and what is on its way. A pure value used by
/// the overview and by decisions; the rules equal those of <c>inventory.approve_transfer</c> (US10) and of the stock
/// ledger trigger (SS200).
/// </summary>
internal sealed record StockPosition(decimal OnHand, decimal Reserved, decimal SafetyStock, decimal? MaxStock, decimal InTransit = 0)
{
    public decimal Available => OnHand - Reserved;

    public StockStatus Status => Available switch
    {
        <= 0 => StockStatus.Out,
        var available when available < SafetyStock => StockStatus.BelowSafety,
        _ when MaxStock is { } max && OnHand > max => StockStatus.AboveMax,
        _ => StockStatus.Ok,
    };

    /// <summary>Whether <paramref name="quantity"/> can be issued without making the available stock negative (SS200).</summary>
    public bool CanIssue(decimal quantity) => quantity > 0 && quantity <= Available;

    /// <summary>
    /// US10: a donor gives only the stock above its safety buffer; an urgent transfer may use the buffer, but never
    /// more than is available (SS201, SS200).
    /// </summary>
    public bool CanGive(decimal quantity, bool isUrgent) =>
        CanIssue(quantity) && (isUrgent || Available - quantity >= SafetyStock);

    /// <summary>The quantity a donor can give without touching its safety stock.</summary>
    public decimal Surplus => Math.Max(0, Available - SafetyStock);
}
