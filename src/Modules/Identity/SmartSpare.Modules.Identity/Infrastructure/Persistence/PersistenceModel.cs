namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

// The Identity tables are mapped from the domain model (Domain/, ADR 0013); only the Platform reference
// table that is the principal of this module's FKs remains a plain persistence class.

/// <summary>Platform reference table <c>ref.brand</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefBrand
{
    public short Id { get; set; }
}
