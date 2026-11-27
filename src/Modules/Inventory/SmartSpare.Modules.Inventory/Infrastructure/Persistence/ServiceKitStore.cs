using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

/// <summary>The quantity <c>inventory.reserve_service_kit</c> holds for one service order line.</summary>
internal sealed record ReservedLine(long ServiceOrderLineId, decimal ReservedQty);

/// <summary>What <c>inventory.issue_service_kit</c> issued for one service order line, and the shortfall.</summary>
internal sealed record IssuedLine(long ServiceOrderLineId, decimal IssuedQty, decimal ShortfallQty);

/// <summary>The SQL behind service kits (STAGE_6_PLAN 5.3): <c>inventory.reserve_service_kit</c>, in the consumer's transaction.</summary>
internal sealed class ServiceKitStore(InventoryDbContext db)
{
    public async Task<IReadOnlyList<ReservedLine>> ReserveAsync(
        short warehouseId, IReadOnlyList<(long LineId, long PartId, decimal Quantity)> lines, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<ReservedLine>(
                "SELECT * FROM inventory.reserve_service_kit(@warehouse, @lines, @parts, @quantities, @expires_at)",
                new NpgsqlParameter<short>("warehouse", warehouseId),
                new NpgsqlParameter<long[]>("lines", [.. lines.Select(l => l.LineId)]),
                new NpgsqlParameter<long[]>("parts", [.. lines.Select(l => l.PartId)]),
                new NpgsqlParameter<decimal[]>("quantities", [.. lines.Select(l => l.Quantity)]),
                new NpgsqlParameter<DateTimeOffset>("expires_at", expiresAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary><c>inventory.expire_soft_reservations</c>: expires the holds overdue at <paramref name="now"/>, returns their lines.</summary>
    public async Task<IReadOnlyList<long>> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<long>(
                "SELECT DISTINCT service_order_line_id AS \"Value\" FROM inventory.expire_soft_reservations(@now) WHERE service_order_line_id IS NOT NULL",
                new NpgsqlParameter<DateTimeOffset>("now", now))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary><c>inventory.issue_service_kit</c>: consumes the holds and issues the open quantities up to the stock.</summary>
    public async Task<IReadOnlyList<IssuedLine>> IssueAsync(
        short warehouseId, IReadOnlyList<(long LineId, long PartId, decimal Quantity)> lines, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<IssuedLine>(
                "SELECT * FROM inventory.issue_service_kit(@warehouse, @lines, @parts, @quantities)",
                new NpgsqlParameter<short>("warehouse", warehouseId),
                new NpgsqlParameter<long[]>("lines", [.. lines.Select(l => l.LineId)]),
                new NpgsqlParameter<long[]>("parts", [.. lines.Select(l => l.PartId)]),
                new NpgsqlParameter<decimal[]>("quantities", [.. lines.Select(l => l.Quantity)]))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
