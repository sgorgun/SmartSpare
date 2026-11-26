using System.ComponentModel;
using System.Globalization;
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

/// <summary>The <c>xmin</c> concurrency version of an aggregate as an opaque string and an entity tag.</summary>
internal static class Versions
{
    public static string Format(uint version) => version.ToString(CultureInfo.InvariantCulture);

    public static string ETag(uint version) => $"\"{Format(version)}\"";

    /// <summary>Reads <c>If-Match: "123"</c> (or a bare <c>123</c>); <see langword="null"/> if absent or malformed.</summary>
    public static uint? Parse(string? ifMatch)
    {
        var value = ifMatch?.Trim();
        if (value is { Length: > 2 } && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;
    }
}

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
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("GetStockPosition")
            .WithSummary("Returns one stock position with its reservations and latest movements.")
            .WithDescription(
                "The overview row of the part in the hub, its shelf location, the open (soft or firm) reservations with the "
                + "transfer order that holds them, and the latest 20 ledger movements. The ETag header and the version field "
                + "carry the concurrency version of the stocking policy, needed to change the stock levels. A part that is not "
                + "stocked in the hub gives 404 with code Inventory.StockPositionNotFound. Policy ReadInventory.")
            .Produces<StockPositionDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.StockPosition)
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.StockPositionNotFound));
}
