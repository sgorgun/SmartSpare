using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>PostgreSQL enum <c>inventory.reservation_status</c>.</summary>
internal enum ReservationStatus
{
    Soft,
    Firm,
    Consumed,
    Released,
    Expired,
}

/// <summary>
/// A stock hold (<c>inventory.stock_reservation</c>). Created soft (with an expiry, service kits) or firm (approved
/// transfers); an open hold is consumed, released or expires, and a closed one never changes again
/// (<c>trg_stock_reservation_guard</c>). Active quantities are summed into the balance by
/// <c>trg_stock_reservation_sync</c>. In stage 5 the SQL transfer functions write the holds; the type serves reads and
/// the service-kit flow of stage 6.
/// </summary>
internal sealed class StockReservation : AggregateRoot<long>
{
    private StockReservation()
        : base(0)
    {
    }

    public short WarehouseId { get; private set; }

    public long PartId { get; private set; }

    public decimal Quantity { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public long? ServiceOrderLineId { get; private set; }

    public long? TransferOrderLineId { get; private set; }

    public Guid? CreatedBy { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public bool IsOpen => Status is ReservationStatus.Soft or ReservationStatus.Firm;

    /// <summary>A soft hold for a service order line, open until <paramref name="expiresAt"/>.</summary>
    public static Result<StockReservation> Soft(
        short warehouseId, long partId, decimal quantity, long serviceOrderLineId, DateTimeOffset expiresAt, Guid createdBy) =>
        Domain.Quantity.Create(quantity).IsFailure
            ? InventoryErrors.InvalidQuantity
            : new StockReservation
            {
                WarehouseId = warehouseId,
                PartId = partId,
                Quantity = quantity,
                Status = ReservationStatus.Soft,
                ExpiresAt = expiresAt,
                ServiceOrderLineId = serviceOrderLineId,
                CreatedBy = createdBy,
            };

    /// <summary>A firm hold for a transfer order line.</summary>
    public static Result<StockReservation> Firm(short warehouseId, long partId, decimal quantity, long transferOrderLineId, Guid createdBy) =>
        Domain.Quantity.Create(quantity).IsFailure
            ? InventoryErrors.InvalidQuantity
            : new StockReservation
            {
                WarehouseId = warehouseId,
                PartId = partId,
                Quantity = quantity,
                Status = ReservationStatus.Firm,
                TransferOrderLineId = transferOrderLineId,
                CreatedBy = createdBy,
            };

    /// <summary>A soft hold becomes firm (it no longer expires).</summary>
    public Result Confirm()
    {
        if (Status != ReservationStatus.Soft)
        {
            return InventoryErrors.InvalidTransition("reservation", Status, "confirmed");
        }

        Status = ReservationStatus.Firm;
        return Result.Success();
    }

    /// <summary>The held stock left the warehouse (dispatch, service issue).</summary>
    public Result Consume(DateTimeOffset now) => Close(ReservationStatus.Consumed, "consumed", now);

    /// <summary>The hold is no longer needed (cancellation).</summary>
    public Result Release(DateTimeOffset now) => Close(ReservationStatus.Released, "released", now);

    /// <summary>A soft hold ran out; a firm hold never expires.</summary>
    public Result Expire(DateTimeOffset now) =>
        Status == ReservationStatus.Firm
            ? InventoryErrors.InvalidTransition("reservation", Status, "expired")
            : Close(ReservationStatus.Expired, "expired", now);

    private Result Close(ReservationStatus target, string action, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return InventoryErrors.InvalidTransition("reservation", Status, action);
        }

        Status = target;
        ClosedAt = now;
        return Result.Success();
    }
}
