using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Features;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Features.GetPart;
using SmartSpare.Modules.Inventory.Features.Imports;
using SmartSpare.Modules.Inventory.Features.ListWarehouses;
using SmartSpare.Modules.Inventory.Features.SearchParts;
using SmartSpare.Modules.Inventory.Features.Stock;
using SmartSpare.Modules.Inventory.Features.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

// Data access of the Inventory read side (STAGE_5_PLAN section 2.4): query handlers and read models that only read
// PostgreSQL, with raw SQL or EF Core projections. They live in Infrastructure; Features keep the queries, their
// validation, the endpoints and the business rules. The unit-test metric of business logic covers Domain and Features
// (tools/check.sh coverage), so this code is measured by the integration tests.

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
        var pattern = string.IsNullOrEmpty(search) ? null : $"%{SearchText.Escape(search)}%";

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
}

internal sealed record PartSummaryRow(
    long Id, string BrandCode, string OemPartNumber, string Description, string CategoryName, string UnitOfMeasure, decimal UnitCost);

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
