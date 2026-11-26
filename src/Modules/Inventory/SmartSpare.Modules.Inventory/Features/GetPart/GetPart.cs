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
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.GetPart;

/// <summary>One part with its reference data and its stock in every warehouse.</summary>
internal sealed record GetPartQuery(long Id) : IQuery<PartDetails>;

internal static class PartErrors
{
    public const string NotFoundCode = "Inventory.PartNotFound";

    public static Error NotFound(long id) => Error.NotFound(NotFoundCode, $"Part {id} does not exist.");
}

internal sealed class GetPartQueryHandler(InventoryDbContext db) : IQueryHandler<GetPartQuery, PartDetails>
{
    public async Task<Result<PartDetails>> Handle(GetPartQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var part = await db.Database
            .SqlQueryRaw<PartDetailsRow>(
                """
                SELECT p.id, b.code AS brand_code, b.name AS brand_name, p.oem_part_number, p.description,
                       c.code AS category_code, c.name AS category_name, u.code AS unit_of_measure,
                       p.weight_kg, p.unit_cost, p.is_condition_based, p.is_active
                FROM catalog.part p
                JOIN ref.brand b ON b.id = p.brand_id
                JOIN ref.part_category c ON c.id = p.category_id
                JOIN ref.unit_of_measure u ON u.id = p.uom_id
                WHERE p.id = @id
                """,
                new NpgsqlParameter<long>("id", query.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (part is null)
        {
            return Result.Failure<PartDetails>(PartErrors.NotFound(query.Id));
        }

        var stock = await db.Set<StockBalance>().AsNoTracking()
            .Where(s => s.PartId == query.Id)
            .Join(db.Set<Warehouse>(), s => s.WarehouseId, w => w.Id, (s, w) => new { Balance = s, w.Code })
            .OrderBy(x => x.Code)
            .Select(x => new WarehouseStock(
                x.Balance.WarehouseId, x.Code, x.Balance.OnHandQty, x.Balance.ReservedQty, x.Balance.OnHandQty - x.Balance.ReservedQty))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new PartDetails(
            part.Id, part.BrandCode, part.BrandName, part.OemPartNumber, part.Description, part.CategoryCode, part.CategoryName,
            part.UnitOfMeasure, part.WeightKg, part.UnitCost, part.IsConditionBased, part.IsActive, stock));
    }
}

internal sealed record PartDetailsRow(
    long Id,
    string BrandCode,
    string BrandName,
    string OemPartNumber,
    string Description,
    string CategoryCode,
    string CategoryName,
    string UnitOfMeasure,
    decimal? WeightKg,
    decimal UnitCost,
    bool IsConditionBased,
    bool IsActive);

internal static class GetPartEndpoint
{
    /// <summary>GET /api/v1/inventory/parts/{id} — one part with its stock.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/parts/{id:long}", async (
                [Description("Part id, as returned by the search.")] long id,
                IQueryHandler<GetPartQuery, PartDetails> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new GetPartQuery(id), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("GetPart")
            .WithSummary("Returns one part with its stock per warehouse.")
            .WithDescription(
                "The part, a deactivated one included, with its brand, category and unit, and its on-hand, reserved and "
                + "available quantity in each warehouse that holds a balance. An unknown id gives 404 with code "
                + "Inventory.PartNotFound. Policy ReadInventory.")
            .Produces<PartDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new PartDetails(
                1042, "JD", "John Deere", "RE900001", "Engine oil filter", "OIL_FILTER", "Engine oil filters", "PCS",
                WeightKg: 0.450m, UnitCost: 18.40m, IsConditionBased: false, IsActive: true,
                Stock: [new WarehouseStock(3, "KLP", OnHand: 24, Reserved: 13, Available: 11), new WarehouseStock(5, "PNV", 2, 0, 2)]))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(
                StatusCodes.Status404NotFound, "Resource not found", PartErrors.NotFoundCode, "Part 999999 does not exist."));
}
