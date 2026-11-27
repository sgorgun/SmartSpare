using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Stock;

/// <summary>The stock overview: stocked parts per hub, filtered by hub, status and text, one page at a time.</summary>
internal sealed record StockOverviewQuery(short? WarehouseId, string? Status, string? Search, int Page, int PageSize)
    : IQuery<PagedResponse<StockRow>>;

internal sealed class StockOverviewQueryValidator : AbstractValidator<StockOverviewQuery>
{
    public StockOverviewQueryValidator()
    {
        RuleFor(q => q.WarehouseId).GreaterThan((short)0).When(q => q.WarehouseId is not null);
        RuleFor(q => q.Status).Must(s => StockStatuses.All.Contains(s!)).When(q => q.Status is not null)
            .WithMessage($"'Status' must be one of: {string.Join(", ", StockStatuses.All)}.");
        RuleFor(q => q.Search).MaximumLength(StockSearch.MaxSearchLength);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, StockSearch.MaxPageSize);
    }
}

internal static class StockOverviewEndpoint
{
    /// <summary>GET /api/v1/inventory/stock — the stock overview.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/stock", async (
                IQueryHandler<StockOverviewQuery, PagedResponse<StockRow>> handler,
                CancellationToken cancellationToken,
                [Description("Hub id; empty lists every hub.")] short? warehouseId = null,
                [Description("Stock status: out, belowSafety, ok or aboveMax; empty lists every status.")] string? status = null,
                [Description("Text contained in the OEM part number or the description, case-insensitive.")] string? search = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Rows per page, 1 to 100.")] int pageSize = StockSearch.DefaultPageSize) =>
            {
                var result = await handler.Handle(new StockOverviewQuery(warehouseId, status, search, page, pageSize), cancellationToken)
                    .ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(Permissions.Inventory.Stock.View)
            .WithName("GetStockOverview")
            .WithSummary("Lists the stock of every stocked part per hub.")
            .WithDescription(
                "One row per hub and stocked part: on hand, reserved, available (on hand minus reserved), the hub's safety and "
                + "maximum stock, the quantity dispatched to the hub by transfers and not yet received (inTransit), and a "
                + "status: out (nothing available), belowSafety (available below the safety stock), aboveMax (on hand above "
                + "the maximum) or ok. Ordered by hub code and OEM part number; totalCount counts all matches. In-transit "
                + "quantities from purchase orders are added in stage 7. Permission Inventory.Stock.View; reads are not brand-scoped.")
            .Produces<PagedResponse<StockRow>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<StockRow>(
                [InventoryExamples.StockRow],
                Page: 1,
                PageSize: StockSearch.DefaultPageSize,
                TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                nameof(StockOverviewQuery.Status), "'Status' must be one of: out, belowSafety, ok, aboveMax."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);
}
