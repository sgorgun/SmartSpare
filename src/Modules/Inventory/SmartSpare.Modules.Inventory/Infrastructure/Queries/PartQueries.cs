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
using SmartSpare.Modules.Inventory.Features.Stock;
using SmartSpare.Modules.Inventory.Features.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

// Data access of the Inventory read side (STAGE_5_PLAN section 2.4): query handlers and read models that only read
// PostgreSQL, with raw SQL or EF Core projections. They live in Infrastructure; Features keep the queries, their
// validation, the endpoints and the business rules. The unit-test metric of business logic covers Domain and Features
// (tools/check.sh coverage), so this code is measured by the integration tests.

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
                       p.weight_kg, p.unit_cost, p.is_condition_based, NOT p.is_archived AS is_active
                FROM catalog.part p
                JOIN catalog.brand b ON b.id = p.brand_id
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
