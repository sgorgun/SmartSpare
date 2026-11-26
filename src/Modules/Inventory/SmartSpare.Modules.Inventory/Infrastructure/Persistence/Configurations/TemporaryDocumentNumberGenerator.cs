using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// Temporary value for a document number that the database assigns on insert (<c>util.trg_assign_document_number</c>
/// fills a NULL number). The number is an alternate key, so EF Core needs a value to track a new row; a temporary
/// value is never written, and the real number is read back after the INSERT.
/// </summary>
internal sealed class TemporaryDocumentNumberGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => true;

    public override string Next(EntityEntry entry) => $"tmp-{Guid.NewGuid():N}";
}
