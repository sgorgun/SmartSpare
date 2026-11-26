using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Temporary value for a document number that the database assigns on insert (<c>util.trg_assign_document_number</c>
/// fills a NULL number). The number is an alternate key, so EF Core needs a value to track a new row; a temporary
/// value is never written, and the real number is read back after the INSERT. Used by the Inventory (TR) and Fleet (SO) documents.
/// </summary>
public sealed class TemporaryDocumentNumberGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => true;

    public override string Next(EntityEntry entry) => $"tmp-{Guid.NewGuid():N}";
}
