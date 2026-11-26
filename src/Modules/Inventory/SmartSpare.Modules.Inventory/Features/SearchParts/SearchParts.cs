using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.SearchParts;

/// <summary>Active catalogue parts whose OEM number or description contains <see cref="Search"/>, one page at a time.</summary>
internal sealed record SearchPartsQuery(string? Search, int Page, int PageSize) : IQuery<PagedResponse<PartSummary>>;

internal sealed class SearchPartsQueryValidator : AbstractValidator<SearchPartsQuery>
{
    public SearchPartsQueryValidator()
    {
        RuleFor(q => q.Search).MaximumLength(PartSearch.MaxSearchLength);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, PartSearch.MaxPageSize);
    }
}

internal static class SearchPartsEndpoint
{
    /// <summary>GET /api/v1/inventory/parts — search the parts catalogue.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/parts", async (
                IQueryHandler<SearchPartsQuery, PagedResponse<PartSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Text contained in the OEM part number or the description, case-insensitive. Empty lists every active part.")] string? search = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Parts per page, 1 to 100.")] int pageSize = PartSearch.DefaultPageSize) =>
            {
                var result = await handler.Handle(new SearchPartsQuery(search, page, pageSize), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("SearchParts")
            .WithSummary("Searches the parts catalogue.")
            .WithDescription(
                "Active parts whose OEM part number or description contains the search text, ordered by OEM part number, "
                + "one page at a time; totalCount is the number of all matches. Every role may read the catalogue "
                + "(policy ReadInventory), and reads are not limited to the user's brands.")
            .Produces<PagedResponse<PartSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<PartSummary>(
                [new PartSummary(1042, "JD", "RE900001", "Engine oil filter", "Engine oil filters", "PCS", 18.40m)],
                Page: 1,
                PageSize: PartSearch.DefaultPageSize,
                TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                nameof(SearchPartsQuery.PageSize), "'Page Size' must be between 1 and 100. You entered 500."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);
}
