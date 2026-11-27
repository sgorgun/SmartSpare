using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Warehouses;

internal static class WarehouseQueriesEndpoint
{
    /// <summary>
    /// GET /internal/v1/inventory/warehouses — <see cref="IWarehouseQueries"/> for other services, with the caller's
    /// internal token. Reading the hubs needs Inventory.Warehouse.View (every default role); the gateway never routes it.
    /// </summary>
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(InventoryInternalApi.Warehouses, async (IWarehouseQueries queries, CancellationToken cancellationToken) =>
                Results.Ok(await queries.GetAllAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(Permissions.Inventory.Warehouse.View)
            .ExcludeFromDescription();
}
