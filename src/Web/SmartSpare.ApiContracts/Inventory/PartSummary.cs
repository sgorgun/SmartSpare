using System.ComponentModel;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>A row of <c>GET /api/v1/inventory/parts</c>: a catalogue part without its stock.</summary>
[Description("A catalogue part without its stock.")]
public sealed record PartSummary(
    [property: Description("Part id.")] long Id,
    [property: Description("Brand code, for example CLAAS or JD.")] string BrandCode,
    [property: Description("Manufacturer (OEM) part number, upper case.")] string OemPartNumber,
    [property: Description("Description of the part.")] string Description,
    [property: Description("Name of the part category.")] string CategoryName,
    [property: Description("Unit of measure code: PCS, SET, L, KG or M.")] string UnitOfMeasure,
    [property: Description("Standard cost per unit in EUR.")] decimal UnitCost);
