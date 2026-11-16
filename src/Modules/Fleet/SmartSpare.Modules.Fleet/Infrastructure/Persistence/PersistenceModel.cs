namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>PostgreSQL enum <c>fleet.machine_status</c>.</summary>
internal enum MachineStatus
{
    Active,
    Inactive,
    Decommissioned,
}

/// <summary>PostgreSQL enum <c>fleet.reading_source</c>.</summary>
internal enum ReadingSource
{
    Manual,
    Telematics,
    ServiceVisit,
    Import,
}

/// <summary>PostgreSQL enum <c>fleet.service_order_status</c>.</summary>
internal enum ServiceOrderStatus
{
    Draft,
    PartsReserved,
    InProgress,
    Completed,
    Cancelled,
}

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

/// <summary>Row of <c>fleet.engine_hours_reading</c>.</summary>
internal sealed class EngineHoursReading
{
    public long Id { get; set; }

    public long MachineId { get; set; }

    public decimal ReadingHours { get; set; }

    public DateTimeOffset ReadAt { get; set; }

    public ReadingSource Source { get; set; }

    public Guid? RecordedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>fleet.machine</c>.</summary>
internal sealed class Machine
{
    public long Id { get; set; }

    public string Vin { get; set; } = null!;

    public int ModelId { get; set; }

    public long CustomerId { get; set; }

    public short OperatingConditionId { get; set; }

    public short HomeWarehouseId { get; set; }

    public short ManufactureYear { get; set; }

    public decimal CurrentEngineHours { get; set; }

    public DateTimeOffset? EngineHoursReadAt { get; set; }

    public MachineStatus Status { get; set; }

    public DateOnly? CommissionedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>fleet.machine_model</c>.</summary>
internal sealed class MachineModel
{
    public int Id { get; set; }

    public short BrandId { get; set; }

    public short MachineTypeId { get; set; }

    public string ModelName { get; set; } = null!;
}

/// <summary>Row of <c>fleet.maintenance_plan</c>.</summary>
internal sealed class MaintenancePlan
{
    public int Id { get; set; }

    public int ModelId { get; set; }

    public int IntervalHours { get; set; }

    public string Name { get; set; } = null!;

    public bool IsActive { get; set; }
}

/// <summary>Row of <c>fleet.maintenance_plan_part</c>.</summary>
internal sealed class MaintenancePlanPart
{
    public int MaintenancePlanId { get; set; }

    public long PartId { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>Row of <c>fleet.service_order</c>.</summary>
internal sealed class ServiceOrder
{
    public long Id { get; set; }

    public string Number { get; set; } = null!;

    public long MachineId { get; set; }

    public int? MaintenancePlanId { get; set; }

    public short WarehouseId { get; set; }

    public ServiceOrderStatus Status { get; set; }

    public decimal? DueEngineHours { get; set; }

    public decimal? TriggerEngineHours { get; set; }

    public decimal? CompletedEngineHours { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>fleet.service_order_line</c>.</summary>
internal sealed class ServiceOrderLine
{
    public long Id { get; set; }

    public long ServiceOrderId { get; set; }

    public long PartId { get; set; }

    public decimal QtyRequired { get; set; }

    public decimal QtyIssued { get; set; }
}

/// <summary>Platform reference table <c>ref.brand</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefBrand
{
    public short Id { get; set; }
}

/// <summary>Platform reference table <c>ref.machine_type</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefMachineType
{
    public short Id { get; set; }
}

/// <summary>Platform reference table <c>ref.operating_condition</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefOperatingCondition
{
    public short Id { get; set; }
}
