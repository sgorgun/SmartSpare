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

internal sealed class GetStockPositionQueryHandler(InventoryDbContext db) : IQueryHandler<GetStockPositionQuery, StockPositionDetails>
{
    public const int RecentMovements = 20;

    public async Task<Result<StockPositionDetails>> Handle(GetStockPositionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stock = await StockRows.FindAsync(db, query.WarehouseId, query.PartId, cancellationToken).ConfigureAwait(false);
        if (stock is null)
        {
            return InventoryErrors.StockPositionNotFound;
        }

        var policy = await db.Set<WarehousePart>().AsNoTracking()
            .Where(p => p.WarehouseId == query.WarehouseId && p.PartId == query.PartId)
            .Select(p => new { p.BinLocation, Version = EF.Property<uint>(p, "xmin") })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var reservations = await db.Database.SqlQueryRaw<ReservationSummary>(
                """
                SELECT r.id, r.quantity, r.status::text AS status, r.expires_at, o.id AS transfer_order_id,
                       o.number AS transfer_order_number, r.service_order_line_id, r.created_at
                FROM inventory.stock_reservation r
                LEFT JOIN inventory.transfer_order_line l ON l.id = r.transfer_order_line_id
                LEFT JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
                WHERE r.warehouse_id = @warehouse AND r.part_id = @part AND r.status IN ('soft', 'firm')
                ORDER BY r.created_at, r.id
                """,
                new NpgsqlParameter<short>("warehouse", query.WarehouseId),
                new NpgsqlParameter<long>("part", query.PartId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var movements = await Movements.PageAsync(
            db, new Movements.Filter(query.WarehouseId, query.PartId, null, null, null), 1, RecentMovements, cancellationToken).ConfigureAwait(false);

        return new StockPositionDetails(stock, policy.BinLocation, Versions.Format(policy.Version), reservations, movements);
    }
}

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
