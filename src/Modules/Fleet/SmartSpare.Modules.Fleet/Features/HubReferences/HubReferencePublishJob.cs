using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Infrastructure.Queries;

namespace SmartSpare.Modules.Fleet.Features.HubReferences;

/// <summary>
/// Publishes the hub of every machine and service order, open or not (ADR 0029): the initial fill of Inventory's
/// <c>inventory.fleet_hub_reference</c> for records that predate the projection, and a repair tool. Chunks of at most
/// <see cref="HubReferencesChanged.MaxReferences"/> records. <c>--job fleet.hub-reference-publish</c> in the services topology.
/// </summary>
internal sealed class HubReferencePublishJob(FleetDbContext db, IUnitOfWork<FleetDbContext> unitOfWork) : IJob
{
    public const string JobName = "fleet.hub-reference-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var references = await HubReferenceRows.AllAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in references.Chunk(HubReferencesChanged.MaxReferences))
        {
            unitOfWork.AddIntegrationEvent(new HubReferencesChanged(chunk));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
