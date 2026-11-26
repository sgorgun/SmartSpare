namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>
/// Current stock of a part in a warehouse (<c>inventory.stock_balance</c>). Controlled redundancy: the sum of the
/// ledger, written only by the triggers <c>trg_stock_movement_apply</c> and <c>trg_stock_reservation_sync</c>; the
/// application reads it and never writes it (no write grant, DATABASE_DESIGN 8.2).
/// </summary>
internal sealed class StockBalance
{
    private StockBalance()
    {
    }

    public short WarehouseId { get; private set; }

    public long PartId { get; private set; }

    public decimal OnHandQty { get; private set; }

    public decimal ReservedQty { get; private set; }

    /// <summary>Generated column <c>on_hand_qty - reserved_qty</c>.</summary>
    public decimal? AvailableQty { get; private set; }

    public DateTimeOffset? LastMovementAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The stock position for decisions (<see cref="StockPosition"/>) with the stocking policy of the hub.</summary>
    public StockPosition ToPosition(WarehousePart policy, decimal inTransitQty = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new StockPosition(OnHandQty, ReservedQty, policy.SafetyStockQty, policy.MaxStockQty, inTransitQty);
    }
}
