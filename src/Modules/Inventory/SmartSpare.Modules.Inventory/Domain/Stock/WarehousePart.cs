using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>
/// Stocking policy of a part in a warehouse (<c>inventory.warehouse_part</c>): shelf location, safety stock and
/// maximum stock. The rules equal <c>ck_warehouse_part_safety</c> and <c>ck_warehouse_part_max</c>; <c>xmin</c> is the
/// concurrency token, so two planners cannot overwrite each other's levels unnoticed.
/// </summary>
internal sealed class WarehousePart
{
    private WarehousePart()
    {
    }

    private WarehousePart(short warehouseId, long partId, decimal safetyStockQty, decimal? maxStockQty)
    {
        WarehouseId = warehouseId;
        PartId = partId;
        SafetyStockQty = safetyStockQty;
        MaxStockQty = maxStockQty;
        IsStocked = true;
    }

    public short WarehouseId { get; private set; }

    public long PartId { get; private set; }

    public string? BinLocation { get; private set; }

    public decimal SafetyStockQty { get; private set; }

    public decimal? MaxStockQty { get; private set; }

    public bool IsStocked { get; private set; }

    /// <summary>Maintained by the <c>updated_at</c> trigger.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Starts stocking a part in a hub with the given levels.</summary>
    public static Result<WarehousePart> Create(short warehouseId, long partId, decimal safetyStockQty, decimal? maxStockQty) =>
        AreValid(safetyStockQty, maxStockQty)
            ? new WarehousePart(warehouseId, partId, safetyStockQty, maxStockQty)
            : InventoryErrors.InvalidStockLevels;

    /// <summary>Changes the safety and maximum stock; the maximum is never below the safety stock.</summary>
    public Result ChangeStockLevels(decimal safetyStockQty, decimal? maxStockQty)
    {
        if (!AreValid(safetyStockQty, maxStockQty))
        {
            return InventoryErrors.InvalidStockLevels;
        }

        SafetyStockQty = safetyStockQty;
        MaxStockQty = maxStockQty;
        return Result.Success();
    }

    private static bool AreValid(decimal safety, decimal? max) =>
        safety >= 0 && safety < Quantity.Limit && decimal.Round(safety, 3) == safety
        && (max is null || (max >= safety && max < Quantity.Limit && decimal.Round(max.Value, 3) == max));
}
