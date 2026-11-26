using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;

namespace SmartSpare.Modules.Inventory.Features.PartCatalog;

/// <summary>
/// How catalogue changes become <see cref="PartCatalogChanged"/> events (STAGE_6_PLAN 5.2, #12): chunks of at most
/// <see cref="PartCatalogChanged.MaxParts"/> parts, so a 20,000-part import stays at 40 messages (ADR 0017 §5).
/// </summary>
internal static class PartCatalogPublication
{
    public static IReadOnlyList<PartCatalogChanged> Events(IReadOnlyList<PartSnapshot> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return [.. parts.Chunk(PartCatalogChanged.MaxParts).Select(chunk => new PartCatalogChanged(chunk))];
    }

    /// <summary>Queues one event per chunk in the unit of work, so the outbox rows commit with the catalogue change.</summary>
    public static void Queue(IUnitOfWork unitOfWork, IReadOnlyList<PartSnapshot> parts)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        foreach (var integrationEvent in Events(parts))
        {
            unitOfWork.AddIntegrationEvent(integrationEvent);
        }
    }
}

/// <summary>
/// Publishes the whole catalogue (STAGE_6_PLAN 5.2): the initial fill of the consumers' copies for a database whose
/// parts predate the consumers, and a repair tool. <c>--job inventory.part-catalog-publish</c> in the services topology.
/// </summary>
internal sealed class PartCatalogPublishJob(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork) : IJob
{
    public const string JobName = "inventory.part-catalog-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var parts = await PartCatalogSnapshots.AllAsync(db, cancellationToken).ConfigureAwait(false);
        PartCatalogPublication.Queue(unitOfWork, parts);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
