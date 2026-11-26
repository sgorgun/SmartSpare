using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Stock;

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

    /// <summary>The search text matches literally: <c>%</c>, <c>_</c> and <c>\</c> lose their LIKE meaning.</summary>
    public static string? Pattern(string? search)
    {
        var text = search?.Trim();
        return string.IsNullOrEmpty(text)
            ? null
            : "%" + text.Replace(@"\", @"\\", StringComparison.Ordinal)
                        .Replace("%", @"\%", StringComparison.Ordinal)
                        .Replace("_", @"\_", StringComparison.Ordinal) + "%";
    }

    private static NpgsqlParameter[] Parameters(Filter filter) =>
    [
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
        new("part", NpgsqlDbType.Bigint) { Value = (object?)filter.PartId ?? DBNull.Value },
        new("status", NpgsqlDbType.Text) { Value = (object?)filter.Status ?? DBNull.Value },
        new("pattern", NpgsqlDbType.Text) { Value = (object?)Pattern(filter.Search) ?? DBNull.Value },
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

    /// <summary>The API name of a domain status (the same strings the SQL above produces).</summary>
    public static string ToApi(StockStatus status) => status switch
    {
        StockStatus.Out => StockStatuses.Out,
        StockStatus.BelowSafety => StockStatuses.BelowSafety,
        StockStatus.AboveMax => StockStatuses.AboveMax,
        _ => StockStatuses.Ok,
    };
}
