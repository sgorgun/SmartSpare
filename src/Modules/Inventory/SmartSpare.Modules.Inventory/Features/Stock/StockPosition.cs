using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Stock;

/// <summary>One stock position with its open reservations and latest movements.</summary>
internal sealed record GetStockPositionQuery(short WarehouseId, long PartId) : IQuery<StockPositionDetails>;

internal static class GetStockPositionEndpoint
{
    /// <summary>GET /api/v1/inventory/stock/{warehouseId}/{partId} — one stock position.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/stock/{warehouseId:int}/{partId:long}", async (
                [Description("Hub id.")] short warehouseId,
                [Description("Part id.")] long partId,
                IQueryHandler<GetStockPositionQuery, StockPositionDetails> handler,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new GetStockPositionQuery(warehouseId, partId), cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    http.Response.Headers.ETag = $"\"{result.Value.Version}\"";
                }

                return result.ToHttpResult();
            })
            .RequireAuthorization(Permissions.Inventory.Stock.View)
            .WithName("GetStockPosition")
            .WithSummary("Returns one stock position with its reservations and latest movements.")
            .WithDescription(
                "The overview row of the part in the hub, its shelf location, the open (soft or firm) reservations with the "
                + "transfer order that holds them, and the latest 20 ledger movements. The ETag header and the version field "
                + "carry the concurrency version of the stocking policy, needed to change the stock levels. A part that is not "
                + "stocked in the hub gives 404 with code Inventory.StockPositionNotFound. Permission Inventory.Stock.View.")
            .Produces<StockPositionDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.StockPosition)
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.StockPositionNotFound));
}
