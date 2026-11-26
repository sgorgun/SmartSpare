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

/// <summary>Reads batches and their errors (CQRS-lite).</summary>
internal static class ImportReader
{
    public static async Task<ImportBatchDetails?> ReadAsync(InventoryDbContext db, long id, CancellationToken cancellationToken)
    {
        var batch = await db.Set<ImportBatch>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        var errors = db.Set<ImportError>().AsNoTracking().Where(e => e.ImportBatchId == id);
        var count = await errors.CountAsync(cancellationToken).ConfigureAwait(false);
        var shown = await errors.OrderBy(e => e.LineNo).ThenBy(e => e.Id).Take(ImportLimits.MaxErrorsShown)
            .Select(e => new ImportRowError(e.LineNo, e.ColumnName, e.ErrorCode, e.Message))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ImportBatchDetails(ImportMapping.ToResponse(batch), shown, count);
    }
}

internal sealed class ListImportsQueryHandler(InventoryDbContext db) : IQueryHandler<ListImportsQuery, PagedResponse<ImportBatchResponse>>
{
    public async Task<Result<PagedResponse<ImportBatchResponse>>> Handle(ListImportsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var batches = db.Set<ImportBatch>().AsNoTracking();
        var total = await batches.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await batches.OrderByDescending(b => b.UploadedAt).ThenByDescending(b => b.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<ImportBatchResponse>([.. page.Select(ImportMapping.ToResponse)], query.Page, query.PageSize, total));
    }
}

internal sealed class GetImportQueryHandler(InventoryDbContext db) : IQueryHandler<GetImportQuery, ImportBatchDetails>
{
    public async Task<Result<ImportBatchDetails>> Handle(GetImportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var details = await ImportReader.ReadAsync(db, query.Id, cancellationToken).ConfigureAwait(false);
        return details is null ? ImportErrors.NotFound : details;
    }
}
