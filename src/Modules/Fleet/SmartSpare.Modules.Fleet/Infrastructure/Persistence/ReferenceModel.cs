namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

// Platform reference tables mapped read-only as principals of this module's FKs (MODULE_MAPPING section 2).

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
