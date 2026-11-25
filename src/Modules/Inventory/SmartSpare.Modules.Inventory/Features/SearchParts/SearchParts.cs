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

/// <summary>
/// Reads straight into the DTO (CQRS-lite): <c>catalog.part</c> with the Platform's reference names from <c>ref</c>. The
/// search is a case-insensitive "contains" on the two columns that have trigram indexes.
/// </summary>
internal sealed class SearchPartsQueryHandler(InventoryDbContext db) : IQueryHandler<SearchPartsQuery, PagedResponse<PartSummary>>
{
    private const string From =
        """
        FROM catalog.part p
        JOIN ref.brand b ON b.id = p.brand_id
        JOIN ref.part_category c ON c.id = p.category_id
        JOIN ref.unit_of_measure u ON u.id = p.uom_id
        WHERE p.is_active
          AND (@pattern IS NULL OR p.oem_part_number ILIKE @pattern OR p.description ILIKE @pattern)
        """;

    private const string Count = "SELECT count(*)::int AS \"Value\"\n";

    private const string Select =
        """
        SELECT p.id, b.code AS brand_code, p.oem_part_number, p.description, c.name AS category_name,
               u.code AS unit_of_measure, p.unit_cost

        """;

    private const string Page = "\nORDER BY p.oem_part_number, b.code\nLIMIT @limit OFFSET @offset";

    private const string CountSql = Count + From;

    private const string PageSql = Select + From + Page;

    public async Task<Result<PagedResponse<PartSummary>>> Handle(SearchPartsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = query.Search?.Trim();
        var pattern = string.IsNullOrEmpty(search) ? null : $"%{EscapeLike(search)}%";

        var total = await db.Database
            .SqlQueryRaw<int>(CountSql, Pattern(pattern))
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.Database
            .SqlQueryRaw<PartSummaryRow>(
                PageSql,
                [
                    Pattern(pattern),
                    new NpgsqlParameter<int>("limit", query.PageSize),
                    new NpgsqlParameter<int>("offset", (query.Page - 1) * query.PageSize),
                ])
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var items = rows
            .Select(r => new PartSummary(r.Id, r.BrandCode, r.OemPartNumber, r.Description, r.CategoryName, r.UnitOfMeasure, r.UnitCost))
            .ToList();
        return Result.Success(new PagedResponse<PartSummary>(items, query.Page, query.PageSize, total));
    }

    /// <summary>Typed as text, so <c>@pattern IS NULL</c> also works without a search.</summary>
    private static NpgsqlParameter Pattern(string? pattern) =>
        new("pattern", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value };

    /// <summary>The search text matches literally: <c>%</c>, <c>_</c> and <c>\</c> lose their LIKE meaning.</summary>
    internal static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}

internal sealed record PartSummaryRow(
    long Id, string BrandCode, string OemPartNumber, string Description, string CategoryName, string UnitOfMeasure, decimal UnitCost);

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
