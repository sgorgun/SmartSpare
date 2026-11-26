using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>A stock position with its open reservations and latest movements.</summary>
[Description("A stock position with its open reservations and latest movements.")]
public sealed record StockPositionDetails(
    [property: Description("The overview row of the position.")] StockRow Stock,
    [property: Description("Shelf location in the hub.")] string? BinLocation,
    [property: Description("Concurrency version of the stocking policy; send it as If-Match when changing the levels.")] string Version,
    [property: Description("Open (soft or firm) reservations, oldest first.")] IReadOnlyList<ReservationSummary> Reservations,
    [property: Description("The latest 20 ledger movements, newest first.")] IReadOnlyList<MovementRow> RecentMovements);

/// <summary>An open stock hold.</summary>
[Description("An open stock hold.")]
public sealed record ReservationSummary(
    [property: Description("Reservation id.")] long Id,
    [property: Description("Held quantity.")] decimal Quantity,
    [property: Description("soft or firm.")] string Status,
    [property: Description("End of a soft hold.")] DateTimeOffset? ExpiresAt,
    [property: Description("Transfer order that holds the stock, if any.")] long? TransferOrderId,
    [property: Description("Number of that transfer order.")] string? TransferOrderNumber,
    [property: Description("Service order line that holds the stock, if any.")] long? ServiceOrderLineId,
    [property: Description("When the hold was placed.")] DateTimeOffset CreatedAt);

/// <summary>A row of the stock ledger.</summary>
[Description("A row of the stock ledger.")]
public sealed record MovementRow(
    [property: Description("Movement id.")] long Id,
    [property: Description("When the movement happened.")] DateTimeOffset OccurredAt,
    [property: Description("Hub id.")] short WarehouseId,
    [property: Description("Hub code.")] string WarehouseCode,
    [property: Description("Part id.")] long PartId,
    [property: Description("Manufacturer part number.")] string OemPartNumber,
    [property: Description("Movement type code, for example TRANSFER_OUT.")] string MovementType,
    [property: Description("Signed effect on the on-hand stock (zero for imported historical demand).")] decimal Quantity,
    [property: Description("Source document: transfer number, external reference or import batch.")] string? Document,
    [property: Description("Note of an adjustment.")] string? Note);

/// <summary>Limits of the movement ledger query.</summary>
public static class MovementSearch
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    /// <summary>Without <c>from</c>, the ledger shows this many days back.</summary>
    public const int DefaultDays = 90;
}

/// <summary>New safety and maximum stock of a part in a hub.</summary>
[Description("New safety and maximum stock of a part in a hub.")]
public sealed record ChangeStockLevelsRequest(
    [property: Description("Safety stock, zero or more.")] decimal SafetyStock,
    [property: Description("Maximum stock, not below the safety stock; empty for no maximum.")] decimal? MaxStock);

public sealed class ChangeStockLevelsRequestValidator : AbstractValidator<ChangeStockLevelsRequest>
{
    public ChangeStockLevelsRequestValidator()
    {
        RuleFor(r => r.SafetyStock).GreaterThanOrEqualTo(0).LessThan(InventoryLimits.MaxQuantity).PrecisionScale(12, 3, true);
        RuleFor(r => r.MaxStock).GreaterThanOrEqualTo(r => r.SafetyStock).LessThan(InventoryLimits.MaxQuantity)
            .PrecisionScale(12, 3, true).When(r => r.MaxStock is not null)
            .WithMessage("'Max Stock' must not be below the safety stock.");
    }
}

/// <summary>A stock count correction in a hub.</summary>
[Description("A stock count correction in a hub.")]
public sealed record StockAdjustmentRequest(
    [property: Description("Hub id.")] short WarehouseId,
    [property: Description("Part id.")] long PartId,
    [property: Description("Signed correction: positive for a surplus, negative for a shortage or scrap.")] decimal Quantity,
    [property: Description("Why the stock is corrected (required).")] string Note);

public sealed class StockAdjustmentRequestValidator : AbstractValidator<StockAdjustmentRequest>
{
    public StockAdjustmentRequestValidator()
    {
        RuleFor(r => r.WarehouseId).GreaterThan((short)0);
        RuleFor(r => r.PartId).GreaterThan(0);
        RuleFor(r => r.Quantity).NotEqual(0).ExclusiveBetween(-InventoryLimits.MaxQuantity, InventoryLimits.MaxQuantity)
            .PrecisionScale(12, 3, true);
        RuleFor(r => r.Note).NotEmpty().MaximumLength(InventoryLimits.MaxNoteLength);
    }
}

/// <summary>A stock movement that was posted.</summary>
[Description("A stock movement that was posted.")]
public sealed record StockAdjustmentResponse(
    [property: Description("Ledger id of the movement.")] long MovementId,
    [property: Description("The position after the movement.")] StockRow Stock);

/// <summary>Limits shared by the Inventory requests (the column types of the database).</summary>
public static class InventoryLimits
{
    /// <summary>Exclusive bound of <c>numeric(12,3)</c>.</summary>
    public const decimal MaxQuantity = 1_000_000_000m;

    public const int MaxNoteLength = 300;
}
