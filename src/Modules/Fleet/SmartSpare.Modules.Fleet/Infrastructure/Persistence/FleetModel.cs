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
