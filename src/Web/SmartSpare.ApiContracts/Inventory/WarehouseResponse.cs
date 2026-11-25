using System.ComponentModel;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>A row of <c>GET /api/v1/inventory/warehouses</c>.</summary>
[Description("A warehouse of the dealer network.")]
public sealed record WarehouseResponse(
    [property: Description("Warehouse id.")] short Id,
    [property: Description("Business code, for example VLN.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("City.")] string City,
    [property: Description("True for the one central hub.")] bool IsCentralHub,
    [property: Description("False when the warehouse is deactivated and kept for history.")] bool IsActive);
