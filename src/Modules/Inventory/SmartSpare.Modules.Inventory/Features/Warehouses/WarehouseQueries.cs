using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Warehouses;

/// <summary>
/// <see cref="IWarehouseQueries"/> answered by the module itself: in process in the single host, and behind
/// <see cref="InventoryInternalApi.Warehouses"/> in the Inventory service (STAGE_5_PLAN section 4.3).
/// </summary>
internal sealed class WarehouseQueries(InventoryDbContext db) : IWarehouseQueries
{
    public async Task<IReadOnlyList<WarehouseInfo>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.Set<Warehouse>().AsNoTracking()
            .OrderBy(w => w.Code)
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.Name, w.IsCentralHub, w.IsActive))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> ExistsAsync(short warehouseId, CancellationToken cancellationToken) =>
        db.Set<Warehouse>().AsNoTracking().AnyAsync(w => w.Id == warehouseId && w.IsActive, cancellationToken);
}

internal static class WarehouseQueriesEndpoint
{
    /// <summary>
    /// GET /internal/v1/inventory/warehouses — <see cref="IWarehouseQueries"/> for other services, with the caller's
    /// internal token. Any signed-in role may read the hubs (policy ReadInventory); the gateway never routes it.
    /// </summary>
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(InventoryInternalApi.Warehouses, async (IWarehouseQueries queries, CancellationToken cancellationToken) =>
                Results.Ok(await queries.GetAllAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(AppPolicies.ReadInventory)
            .ExcludeFromDescription();
}
