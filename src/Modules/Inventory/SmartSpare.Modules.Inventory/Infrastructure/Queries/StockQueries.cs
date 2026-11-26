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
/// The stock overview read model (CQRS-lite, STAGE_5_PLAN section 2.4): every stocked part per hub from
/// <c>warehouse_part</c> with its balance, the parts dispatched to the hub by transfers, and the status of
/// <see cref="StockPosition"/>. The transitional view <c>inventory.v_stock_overview</c> is not used: it reads Procurement
/// data and is replaced in stage 7. The status is computed in SQL so it can be filtered and paged; a test keeps it equal
/// to <see cref="StockPosition.Status"/>.
/// </summary>
internal static class StockRows
{
    private const string From =
        """
        FROM inventory.warehouse_part wp
        JOIN inventory.warehouse w ON w.id = wp.warehouse_id
        JOIN catalog.part p ON p.id = wp.part_id
        JOIN ref.brand b ON b.id = p.brand_id
        JOIN ref.unit_of_measure u ON u.id = p.uom_id
        LEFT JOIN inventory.stock_balance sb ON sb.warehouse_id = wp.warehouse_id AND sb.part_id = wp.part_id
        LEFT JOIN LATERAL (
            SELECT sum(l.quantity) AS qty
            FROM inventory.transfer_order o
            JOIN inventory.transfer_order_line l ON l.transfer_order_id = o.id
            WHERE o.status = 'dispatched' AND o.to_warehouse_id = wp.warehouse_id AND l.part_id = wp.part_id
        ) t ON true
        CROSS JOIN LATERAL (
            SELECT coalesce(sb.on_hand_qty, 0) AS on_hand, coalesce(sb.reserved_qty, 0) AS reserved,
                   coalesce(sb.on_hand_qty, 0) - coalesce(sb.reserved_qty, 0) AS available
        ) q
        CROSS JOIN LATERAL (
            SELECT CASE WHEN q.available <= 0 THEN 'out'
                        WHEN q.available < wp.safety_stock_qty THEN 'belowSafety'
                        WHEN wp.max_stock_qty IS NOT NULL AND q.on_hand > wp.max_stock_qty THEN 'aboveMax'
                        ELSE 'ok' END AS status
        ) s
        WHERE wp.is_stocked
          AND (@warehouse IS NULL OR wp.warehouse_id = @warehouse)
          AND (@part IS NULL OR wp.part_id = @part)
          AND (@status IS NULL OR s.status = @status)
          AND (@pattern IS NULL OR p.oem_part_number ILIKE @pattern OR p.description ILIKE @pattern)
        """;

    private const string Select =
        """
        SELECT wp.warehouse_id, w.code AS warehouse_code, wp.part_id, b.code AS brand_code, p.oem_part_number, p.description,
               u.code AS unit_of_measure, q.on_hand, q.reserved, q.available, wp.safety_stock_qty AS safety_stock,
               wp.max_stock_qty AS max_stock, coalesce(t.qty, 0) AS in_transit, s.status, sb.last_movement_at

        """;

    private const string CountSql = "SELECT count(*)::int AS \"Value\"\n" + From;

    private const string PageSql = Select + From + "\nORDER BY w.code, p.oem_part_number\nLIMIT @limit OFFSET @offset";

    public sealed record Filter(short? WarehouseId = null, long? PartId = null, string? Status = null, string? Search = null);

    public static Task<int> CountAsync(InventoryDbContext db, Filter filter, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(CountSql, Parameters(filter)).SingleAsync(cancellationToken);

    public static async Task<IReadOnlyList<StockRow>> PageAsync(
        InventoryDbContext db, Filter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<Row>(
                PageSql,
                [.. Parameters(filter), new NpgsqlParameter<int>("limit", pageSize), new NpgsqlParameter<int>("offset", (page - 1) * pageSize)])
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToDto())];
    }

    /// <summary>One position, or <see langword="null"/> if the part is not stocked in the hub.</summary>
    public static async Task<StockRow?> FindAsync(InventoryDbContext db, short warehouseId, long partId, CancellationToken cancellationToken) =>
        (await PageAsync(db, new Filter(warehouseId, partId), 1, 1, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    private static NpgsqlParameter[] Parameters(Filter filter) =>
    [
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
        new("part", NpgsqlDbType.Bigint) { Value = (object?)filter.PartId ?? DBNull.Value },
        new("status", NpgsqlDbType.Text) { Value = (object?)filter.Status ?? DBNull.Value },
        new("pattern", NpgsqlDbType.Text) { Value = (object?)SearchText.Pattern(filter.Search) ?? DBNull.Value },
    ];

    internal sealed record Row(
        short WarehouseId,
        string WarehouseCode,
        long PartId,
        string BrandCode,
        string OemPartNumber,
        string Description,
        string UnitOfMeasure,
        decimal OnHand,
        decimal Reserved,
        decimal Available,
        decimal SafetyStock,
        decimal? MaxStock,
        decimal InTransit,
        string Status,
        DateTimeOffset? LastMovementAt)
    {
        public StockRow ToDto() => new(
            WarehouseId, WarehouseCode, PartId, BrandCode, OemPartNumber, Description, UnitOfMeasure, OnHand, Reserved, Available,
            SafetyStock, MaxStock, InTransit, Status, LastMovementAt);
    }
}

/// <summary>The stock ledger read model: movements newest first, with their source document.</summary>
internal static class Movements
{
    private const string FromClause =
        """
        FROM inventory.stock_movement m
        JOIN inventory.warehouse w ON w.id = m.warehouse_id
        JOIN catalog.part p ON p.id = m.part_id
        JOIN ref.movement_type mt ON mt.code = m.movement_type_code
        LEFT JOIN inventory.transfer_order_line l ON l.id = m.transfer_order_line_id
        LEFT JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
        WHERE (@warehouse IS NULL OR m.warehouse_id = @warehouse)
          AND (@part IS NULL OR m.part_id = @part)
          AND (@type IS NULL OR m.movement_type_code = @type)
          AND (@from IS NULL OR m.occurred_at >= @from)
          AND (@to IS NULL OR m.occurred_at < @to)
        """;

    private const string PageSql =
        """
        SELECT m.id, m.occurred_at, m.warehouse_id, w.code AS warehouse_code, m.part_id, p.oem_part_number,
               m.movement_type_code AS movement_type, mt.balance_sign * m.quantity AS quantity,
               coalesce(o.number, m.external_doc_ref,
                        CASE WHEN m.import_batch_id IS NOT NULL THEN 'Import ' || m.import_batch_id END,
                        CASE WHEN m.service_order_line_id IS NOT NULL THEN 'Service order line ' || m.service_order_line_id END,
                        CASE WHEN m.purchase_order_line_id IS NOT NULL THEN 'Purchase order line ' || m.purchase_order_line_id END) AS document,
               m.note

        """ + FromClause + "\nORDER BY m.occurred_at DESC, m.id DESC\nLIMIT @limit OFFSET @offset";

    private const string CountSql = "SELECT count(*)::int AS \"Value\"\n" + FromClause;

    public sealed record Filter(short? WarehouseId, long? PartId, string? MovementType, DateTimeOffset? From, DateTimeOffset? To);

    public static Task<int> CountAsync(InventoryDbContext db, Filter filter, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(CountSql, Parameters(filter)).SingleAsync(cancellationToken);

    public static async Task<IReadOnlyList<MovementRow>> PageAsync(
        InventoryDbContext db, Filter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<MovementRow>(
                PageSql,
                [.. Parameters(filter), new NpgsqlParameter<int>("limit", pageSize), new NpgsqlParameter<int>("offset", (page - 1) * pageSize)])
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static NpgsqlParameter[] Parameters(Filter filter) =>
    [
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
        new("part", NpgsqlDbType.Bigint) { Value = (object?)filter.PartId ?? DBNull.Value },
        new("type", NpgsqlDbType.Text) { Value = (object?)filter.MovementType ?? DBNull.Value },
        new("from", NpgsqlDbType.TimestampTz) { Value = (object?)filter.From?.ToUniversalTime() ?? DBNull.Value },
        new("to", NpgsqlDbType.TimestampTz) { Value = (object?)filter.To?.ToUniversalTime() ?? DBNull.Value },
    ];
}

internal sealed class StockOverviewQueryHandler(InventoryDbContext db) : IQueryHandler<StockOverviewQuery, PagedResponse<StockRow>>
{
    public async Task<Result<PagedResponse<StockRow>>> Handle(StockOverviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = new StockRows.Filter(query.WarehouseId, Status: query.Status, Search: query.Search);
        var total = await StockRows.CountAsync(db, filter, cancellationToken).ConfigureAwait(false);
        var items = await StockRows.PageAsync(db, filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<StockRow>(items, query.Page, query.PageSize, total));
    }
}

internal sealed class GetStockPositionQueryHandler(InventoryDbContext db) : IQueryHandler<GetStockPositionQuery, StockPositionDetails>
{
    public const int RecentMovements = 20;

    public async Task<Result<StockPositionDetails>> Handle(GetStockPositionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stock = await StockRows.FindAsync(db, query.WarehouseId, query.PartId, cancellationToken).ConfigureAwait(false);
        if (stock is null)
        {
            return InventoryErrors.StockPositionNotFound;
        }

        var policy = await db.Set<WarehousePart>().AsNoTracking()
            .Where(p => p.WarehouseId == query.WarehouseId && p.PartId == query.PartId)
            .Select(p => new { p.BinLocation, Version = EF.Property<uint>(p, "xmin") })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var reservations = await db.Database.SqlQueryRaw<ReservationSummary>(
                """
                SELECT r.id, r.quantity, r.status::text AS status, r.expires_at, o.id AS transfer_order_id,
                       o.number AS transfer_order_number, r.service_order_line_id, r.created_at
                FROM inventory.stock_reservation r
                LEFT JOIN inventory.transfer_order_line l ON l.id = r.transfer_order_line_id
                LEFT JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
                WHERE r.warehouse_id = @warehouse AND r.part_id = @part AND r.status IN ('soft', 'firm')
                ORDER BY r.created_at, r.id
                """,
                new NpgsqlParameter<short>("warehouse", query.WarehouseId),
                new NpgsqlParameter<long>("part", query.PartId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var movements = await Movements.PageAsync(
            db, new Movements.Filter(query.WarehouseId, query.PartId, null, null, null), 1, RecentMovements, cancellationToken).ConfigureAwait(false);

        return new StockPositionDetails(stock, policy.BinLocation, Versions.Format(policy.Version), reservations, movements);
    }
}

internal sealed class ListMovementsQueryHandler(InventoryDbContext db, TimeProvider clock)
    : IQueryHandler<ListMovementsQuery, PagedResponse<MovementRow>>
{
    public async Task<Result<PagedResponse<MovementRow>>> Handle(ListMovementsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A lower bound keeps the query on recent partitions of the ledger (partition pruning by occurred_at).
        var from = query.From ?? clock.GetUtcNow().AddDays(-MovementSearch.DefaultDays);
        var filter = new Movements.Filter(query.WarehouseId, query.PartId, query.MovementType, from, query.To);
        var total = await Movements.CountAsync(db, filter, cancellationToken).ConfigureAwait(false);
        var items = await Movements.PageAsync(db, filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<MovementRow>(items, query.Page, query.PageSize, total));
    }
}
