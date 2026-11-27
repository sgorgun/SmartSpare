using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.ListWarehouses;

/// <summary>Every warehouse, the central hub first.</summary>
internal sealed record ListWarehousesQuery : IQuery<IReadOnlyList<WarehouseResponse>>;

internal static class ListWarehousesEndpoint
{
    /// <summary>GET /api/v1/inventory/warehouses — the warehouses of the dealer network.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/warehouses", async (IQueryHandler<ListWarehousesQuery, IReadOnlyList<WarehouseResponse>> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new ListWarehousesQuery(), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(Permissions.Inventory.Warehouse.View)
            .WithName("ListWarehouses")
            .WithSummary("Lists the warehouses.")
            .WithDescription("All warehouses, deactivated ones included, the central hub first, then by code. Permission Inventory.Warehouse.View.")
            .Produces<IReadOnlyList<WarehouseResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[]
            {
                new WarehouseResponse(1, "VLN", "Vilnius Central Hub", "Vilnius", IsCentralHub: true, IsActive: true),
                new WarehouseResponse(3, "KLP", "Klaipėda Regional Depot", "Klaipėda", IsCentralHub: false, IsActive: true),
            })
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, OpenApiConventions.Problem(
                StatusCodes.Status403Forbidden, "Forbidden", ProblemCodes.Forbidden));
}
