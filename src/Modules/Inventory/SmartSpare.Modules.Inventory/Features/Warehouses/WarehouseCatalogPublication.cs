using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Warehouses;

/// <summary>How warehouse changes become <see cref="WarehouseCatalogChanged"/> events (ADR 0029, PR 7j).</summary>
internal static class WarehouseCatalogPublication
{
    /// <summary>Queues one event in the unit of work, so the outbox row commits with the warehouse change.</summary>
    public static void Queue(IUnitOfWork unitOfWork, IReadOnlyList<WarehouseSnapshot> warehouses)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(warehouses);
        if (warehouses.Count > 0)
        {
            unitOfWork.AddIntegrationEvent(new WarehouseCatalogChanged(warehouses));
        }
    }
}

/// <summary>
/// Publishes every warehouse (ADR 0029): the initial fill of Fleet's and Identity's copies, and a repair tool.
/// <c>--job inventory.warehouse-catalog-publish</c> in the services topology.
/// </summary>
internal sealed class WarehouseCatalogPublishJob(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork) : IJob
{
    public const string JobName = "inventory.warehouse-catalog-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var warehouses = await db.Set<Warehouse>().AsNoTracking().OrderBy(w => w.Id)
            .Select(w => new WarehouseSnapshot(w.Id, w.Code, w.Name, w.IsCentralHub, !w.IsActive))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        WarehouseCatalogPublication.Queue(unitOfWork, warehouses);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
