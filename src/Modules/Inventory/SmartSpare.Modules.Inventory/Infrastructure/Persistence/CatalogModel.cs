namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// Warehouses and routes: plain data classes (ADR 0013 rule 3); warehouses and routes are seeded reference
// data of the module. Parts and their analogues are Domain.Catalog aggregates (PR 7i1, 7i2).

/// <summary>Row of <c>inventory.warehouse</c>.</summary>
internal sealed class Warehouse
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string City { get; set; } = null!;

    public string? Address { get; set; }

    public bool IsCentralHub { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>inventory.warehouse_route</c>.</summary>
internal sealed class WarehouseRoute
{
    public short FromWarehouseId { get; set; }

    public short ToWarehouseId { get; set; }

    public decimal DistanceKm { get; set; }

    public decimal TransitHours { get; set; }

    public decimal TransportCostEur { get; set; }
}

/// <summary>Row of <c>inventory.demand_exception</c>.</summary>
#pragma warning disable S2166 // An exceptional demand (US08), named after its table; not a System.Exception.
internal sealed class DemandException
{
    public long MovementId { get; set; }

    public DateTimeOffset MovementOccurredAt { get; set; }

    public string Reason { get; set; } = null!;

    public string DetectionMethod { get; set; } = null!;

    public Guid? FlaggedBy { get; set; }

    public DateTimeOffset FlaggedAt { get; set; }
}
#pragma warning restore S2166
