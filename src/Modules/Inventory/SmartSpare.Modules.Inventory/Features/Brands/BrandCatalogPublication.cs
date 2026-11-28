using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Brands;

/// <summary>How brand changes become <see cref="BrandCatalogChanged"/> events (STAGE_7_PLAN 3.2, PR 7h1).</summary>
internal static class BrandCatalogPublication
{
    public static BrandSnapshot Snapshot(Brand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        return new BrandSnapshot(brand.Id, brand.Code, brand.Name, brand.IsArchived);
    }

    /// <summary>Queues one event in the unit of work, so the outbox row commits with the brand change.</summary>
    public static void Queue(IUnitOfWork unitOfWork, IReadOnlyList<BrandSnapshot> brands)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(brands);
        if (brands.Count > 0)
        {
            unitOfWork.AddIntegrationEvent(new BrandCatalogChanged(brands));
        }
    }
}

/// <summary>
/// Publishes every brand (STAGE_7_PLAN 3.2): the initial fill of Fleet's and Identity's copies after the move, and a
/// repair tool. <c>--job inventory.brand-catalog-publish</c> in the services topology.
/// </summary>
internal sealed class BrandCatalogPublishJob(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork) : IJob
{
    public const string JobName = "inventory.brand-catalog-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var brands = await db.Set<Brand>().AsNoTracking().OrderBy(b => b.Id)
            .Select(b => new BrandSnapshot(b.Id, b.Code, b.Name, b.IsArchived)).ToListAsync(cancellationToken).ConfigureAwait(false);
        BrandCatalogPublication.Queue(unitOfWork, brands);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
