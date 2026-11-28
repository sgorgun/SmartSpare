namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// Platform reference tables mapped read-only as the principals of this module's FKs.

// ref.part_category and ref.unit_of_measure are Domain.Catalog.PartCategory and UnitOfMeasure (a part shows them).

/// <summary>
/// Platform reference table <c>ref.assignment_role</c> (ADR 0026), mapped read-only: the principal of the responsibles'
/// FKs, and which roles grant scope.
/// </summary>
internal sealed class RefAssignmentRole
{
    public string RecordKind { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool GrantsScope { get; set; }
}
