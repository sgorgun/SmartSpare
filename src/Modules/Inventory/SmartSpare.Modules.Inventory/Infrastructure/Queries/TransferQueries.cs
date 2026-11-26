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

/// <summary>SQL read model of transfer orders (CQRS-lite).</summary>
internal static class TransferRows
{
    private const string From =
        """
        FROM inventory.transfer_order o
        JOIN inventory.warehouse f ON f.id = o.from_warehouse_id
        JOIN inventory.warehouse t ON t.id = o.to_warehouse_id
        CROSS JOIN LATERAL (
            SELECT count(*)::int AS line_count, coalesce(sum(l.quantity), 0) AS total_quantity
            FROM inventory.transfer_order_line l WHERE l.transfer_order_id = o.id
        ) l
        WHERE (@id IS NULL OR o.id = @id)
          AND (@status IS NULL OR o.status = @status::inventory.transfer_status)
          AND (@warehouse IS NULL OR o.from_warehouse_id = @warehouse OR o.to_warehouse_id = @warehouse)
        """;

    private const string PageSql =
        """
        SELECT o.id, o.number, o.from_warehouse_id, f.code AS from_warehouse_code, o.to_warehouse_id, t.code AS to_warehouse_code,
               o.status::text AS status, o.is_urgent, l.line_count, l.total_quantity, o.created_at, o.updated_at

        """ + From + "\nORDER BY o.created_at DESC, o.id DESC\nLIMIT @limit OFFSET @offset";

    private const string CountSql = "SELECT count(*)::int AS \"Value\"\n" + From;

    public sealed record Filter(long? Id = null, string? Status = null, short? WarehouseId = null);

    public static Task<int> CountAsync(InventoryDbContext db, Filter filter, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(CountSql, Parameters(filter)).SingleAsync(cancellationToken);

    public static async Task<IReadOnlyList<TransferSummary>> PageAsync(
        InventoryDbContext db, Filter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<TransferSummary>(
                PageSql,
                [.. Parameters(filter), new NpgsqlParameter<int>("limit", pageSize), new NpgsqlParameter<int>("offset", (page - 1) * pageSize)])
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public static async Task<IReadOnlyList<TransferLineResponse>> LinesAsync(InventoryDbContext db, long id, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<TransferLineResponse>(
                """
                SELECT l.id, l.part_id, b.code AS brand_code, p.oem_part_number, p.description, u.code AS unit_of_measure,
                       l.quantity, l.proposed_qty, l.override_reason, coalesce(sb.available_qty, 0) AS donor_available
                FROM inventory.transfer_order_line l
                JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
                JOIN catalog.part p ON p.id = l.part_id
                JOIN ref.brand b ON b.id = p.brand_id
                JOIN ref.unit_of_measure u ON u.id = p.uom_id
                LEFT JOIN inventory.stock_balance sb ON sb.warehouse_id = o.from_warehouse_id AND sb.part_id = l.part_id
                WHERE l.transfer_order_id = @id
                ORDER BY p.oem_part_number
                """,
                new NpgsqlParameter<long>("id", id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static NpgsqlParameter[] Parameters(Filter filter) =>
    [
        new("id", NpgsqlDbType.Bigint) { Value = (object?)filter.Id ?? DBNull.Value },
        new("status", NpgsqlDbType.Text) { Value = (object?)filter.Status ?? DBNull.Value },
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
    ];
}

internal sealed class ListTransfersQueryHandler(InventoryDbContext db) : IQueryHandler<ListTransfersQuery, PagedResponse<TransferSummary>>
{
    public async Task<Result<PagedResponse<TransferSummary>>> Handle(ListTransfersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = new TransferRows.Filter(Status: query.Status, WarehouseId: query.WarehouseId);
        var total = await TransferRows.CountAsync(db, filter, cancellationToken).ConfigureAwait(false);
        var items = await TransferRows.PageAsync(db, filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<TransferSummary>(items, query.Page, query.PageSize, total));
    }
}

internal sealed class GetTransferQueryHandler(TransferDetailsReader reader) : IQueryHandler<GetTransferQuery, TransferDetails>
{
    public async Task<Result<TransferDetails>> Handle(GetTransferQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var details = await reader.ReadAsync(query.Id, cancellationToken).ConfigureAwait(false);
        return details is null ? InventoryErrors.TransferNotFound : details;
    }
}
