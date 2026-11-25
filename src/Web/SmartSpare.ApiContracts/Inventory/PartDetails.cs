using System.ComponentModel;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>Body of <c>GET /api/v1/inventory/parts/{id}</c>: the part, its reference data and its stock per warehouse.</summary>
[Description("A part with its reference data and its stock per warehouse.")]
public sealed record PartDetails(
    [property: Description("Part id.")] long Id,
    [property: Description("Brand code, for example JD.")] string BrandCode,
    [property: Description("Brand name.")] string BrandName,
    [property: Description("Manufacturer (OEM) part number, upper case.")] string OemPartNumber,
    [property: Description("Description of the part.")] string Description,
    [property: Description("Part category code, for example OIL_FILTER.")] string CategoryCode,
    [property: Description("Part category name.")] string CategoryName,
    [property: Description("Unit of measure code: PCS, SET, L, KG or M.")] string UnitOfMeasure,
    [property: Description("Weight in kilograms, if known.")] decimal? WeightKg,
    [property: Description("Standard cost per unit in EUR.")] decimal UnitCost,
    [property: Description("True for parts replaced as required (air and cab filters): their demand comes only from forecasts.")] bool IsConditionBased,
    [property: Description("False when the part is deactivated and kept for history.")] bool IsActive,
    [property: Description("Stock in each warehouse that holds a balance of the part, by warehouse code.")] IReadOnlyList<WarehouseStock> Stock);

/// <summary>Stock of one part in one warehouse.</summary>
[Description("Stock of one part in one warehouse.")]
public sealed record WarehouseStock(
    [property: Description("Warehouse id.")] short WarehouseId,
    [property: Description("Warehouse code, for example KLP.")] string WarehouseCode,
    [property: Description("Quantity on hand.")] decimal OnHand,
    [property: Description("Quantity reserved for service orders and transfers.")] decimal Reserved,
    [property: Description("Quantity available: on hand minus reserved.")] decimal Available);
