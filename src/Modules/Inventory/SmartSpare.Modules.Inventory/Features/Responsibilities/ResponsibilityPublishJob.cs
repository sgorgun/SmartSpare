using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Responsibilities;

/// <summary>
/// Republishes the rights-bearing responsible employees of every brand and warehouse (STAGE_7_PLAN 1.3): the repair tool
/// for a lost <see cref="ResponsibilitiesChanged"/>, and the initial fill of Identity's projection. Records without any
/// rights-bearing assignment are published with an empty set, so Identity drops stale rows too. Acts as the system user
/// (ADR 0024). <c>--job inventory.responsibility-publish</c> in the services topology.
/// </summary>
internal sealed class ResponsibilityPublishJob(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork) : IJob
{
    public const string JobName = "inventory.responsibility-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var scope = await db.Set<RefAssignmentRole>().AsNoTracking().Where(r => r.GrantsScope).Select(r => new { r.RecordKind, r.Code })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool Grants(string kind, string role) => scope.Exists(r => r.RecordKind == kind && r.Code == role);

        var brands = await db.Set<Domain.Catalog.Brand>().AsNoTracking().Select(b => b.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var brandRows = await db.Set<BrandResponsible>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var brand in brands)
        {
            Queue(RecordKinds.Brand, brand, brandRows.Where(r => r.BrandId == brand && Grants(RecordKinds.Brand, r.Role)));
        }

        var warehouses = await db.Set<Warehouse>().AsNoTracking().Select(w => w.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var warehouseRows = await db.Set<WarehouseResponsible>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var warehouse in warehouses)
        {
            Queue(RecordKinds.Warehouse, warehouse, warehouseRows.Where(r => r.WarehouseId == warehouse && Grants(RecordKinds.Warehouse, r.Role)));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Queue(string kind, short recordId, IEnumerable<IResponsibility> rows) =>
        unitOfWork.AddIntegrationEvent(new ResponsibilitiesChanged(
            kind, recordId, [.. rows.OrderBy(r => r.EmployeeId).ThenBy(r => r.Role, StringComparer.Ordinal).Select(r => new ResponsibleAssignment(r.EmployeeId, r.Role))],
            SystemUser.Id));
}
