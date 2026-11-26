namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// Platform reference tables mapped read-only as the principals of this module's FKs.

/// <summary>Platform reference table <c>ref.brand</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefBrand
{
    public short Id { get; set; }
}

/// <summary>Platform reference table <c>ref.part_category</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefPartCategory
{
    public int Id { get; set; }
}

/// <summary>Platform reference table <c>ref.unit_of_measure</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefUnitOfMeasure
{
    public short Id { get; set; }
}
