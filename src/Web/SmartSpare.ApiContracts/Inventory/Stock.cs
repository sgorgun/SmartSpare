using System.ComponentModel;
namespace SmartSpare.ApiContracts.Inventory;

/// <summary>How the stock of a part in a hub compares with its stocking policy (values of <see cref="StockRow.Status"/>).</summary>
public static class StockStatuses
{
    /// <summary>Nothing available: everything on hand is reserved, or nothing is on hand.</summary>
    public const string Out = "out";

    /// <summary>Available stock is below the safety stock.</summary>
    public const string BelowSafety = "belowSafety";

    /// <summary>Between the safety stock and the maximum.</summary>
    public const string Ok = "ok";

    /// <summary>On-hand stock is above the maximum.</summary>
    public const string AboveMax = "aboveMax";

    public static IReadOnlyList<string> All { get; } = [Out, BelowSafety, Ok, AboveMax];
}

/// <summary>Limits of the stock overview.</summary>
public static class StockSearch
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxSearchLength = 100;
}

/// <summary>One part stocked in one hub: balance, stocking policy and what is on its way by transfer.</summary>
[Description("One part stocked in one hub: balance, stocking policy and what is on its way by transfer.")]
public sealed record StockRow(
    [property: Description("Hub id.")] short WarehouseId,
    [property: Description("Hub code, for example KLP.")] string WarehouseCode,
    [property: Description("Part id.")] long PartId,
    [property: Description("Brand code of the part.")] string BrandCode,
    [property: Description("Manufacturer part number.")] string OemPartNumber,
    [property: Description("Part description.")] string Description,
    [property: Description("Unit of measure code.")] string UnitOfMeasure,
    [property: Description("Physical quantity in the hub.")] decimal OnHand,
    [property: Description("Quantity held by open reservations.")] decimal Reserved,
    [property: Description("On hand minus reserved.")] decimal Available,
    [property: Description("Safety stock of the hub.")] decimal SafetyStock,
    [property: Description("Maximum stock of the hub, if set.")] decimal? MaxStock,
    [property: Description("Quantity dispatched to the hub by transfers and not yet received.")] decimal InTransit,
    [property: Description("One of StockStatuses.")] string Status,
    [property: Description("Time of the last physical movement.")] DateTimeOffset? LastMovementAt);
