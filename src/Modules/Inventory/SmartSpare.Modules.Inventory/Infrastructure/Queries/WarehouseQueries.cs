using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain;
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

internal sealed class ListWarehousesQueryHandler(InventoryDbContext db) : IQueryHandler<ListWarehousesQuery, IReadOnlyList<WarehouseResponse>>
{
    public async Task<Result<IReadOnlyList<WarehouseResponse>>> Handle(ListWarehousesQuery query, CancellationToken cancellationToken)
    {
        var warehouses = await db.Set<Warehouse>().AsNoTracking()
            .OrderByDescending(w => w.IsCentralHub).ThenBy(w => w.Code)
            .Select(w => new WarehouseResponse(w.Id, w.Code, w.Name, w.City, w.IsCentralHub, w.IsActive))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<WarehouseResponse>>(warehouses);
    }
}

/// <summary>
/// <see cref="IWarehouseQueries"/> answered by the module itself: in process in the single host, and behind
/// <see cref="InventoryInternalApi.Warehouses"/> in the Inventory service (STAGE_5_PLAN section 4.3).
/// </summary>
internal sealed class WarehouseQueries(InventoryDbContext db) : IWarehouseQueries
{
    public async Task<IReadOnlyList<WarehouseInfo>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.Set<Warehouse>().AsNoTracking()
            .OrderBy(w => w.Code)
            .Select(w => new WarehouseInfo(w.Id, w.Code, w.Name, w.IsCentralHub, w.IsActive))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> ExistsAsync(short warehouseId, CancellationToken cancellationToken) =>
        db.Set<Warehouse>().AsNoTracking().AnyAsync(w => w.Id == warehouseId && w.IsActive, cancellationToken);
}
