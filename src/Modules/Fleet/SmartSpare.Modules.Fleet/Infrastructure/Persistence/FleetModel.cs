namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

// Data without behaviour (ADR 0013 rule 3): customers are pseudonymised master data, machine models are OEM reference
// data. Both stay plain classes; the aggregates live in Domain/.

/// <summary>Row of <c>fleet.customer</c>.</summary>
internal sealed class Customer
{
    public long Id { get; set; }

    public string CustomerCode { get; set; } = null!;

    public byte[]? ErpRefHash { get; set; }

    public short RegionWarehouseId { get; set; }

    public decimal? FarmSizeHa { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>fleet.machine_model</c>.</summary>
internal sealed class MachineModel
{
    public int Id { get; set; }

    public short BrandId { get; set; }

    public short MachineTypeId { get; set; }

    public string ModelName { get; set; } = null!;
}

/// <summary>
/// Row of <c>fleet.part_snapshot</c>: Fleet's read-only copy of an Inventory part (MODULE_MAPPING 2.3, STAGE_6_PLAN 5.2),
/// written only by the consumer of <c>PartCatalogChanged</c>. <see cref="SourceOccurredAt"/> is the time of the event
/// that wrote the row, so a redelivered or late event never overwrites newer data.
/// </summary>
/// <summary>Read-only copy of an Inventory brand (STAGE_7_PLAN 3.2), written only by the BrandCatalogChanged consumer.</summary>
internal sealed class BrandSnapshot
{
    public short BrandId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsArchived { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class PartSnapshot
{
    public long PartId { get; set; }

    public string BrandCode { get; set; } = null!;

    public string OemPartNumber { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string UnitOfMeasure { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Read-only copy of an Inventory warehouse (ADR 0029), written only through <c>fleet.apply_warehouses</c>.</summary>
internal sealed class WarehouseSnapshot
{
    public short WarehouseId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsCentralHub { get; set; }

    public bool IsArchived { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
