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
