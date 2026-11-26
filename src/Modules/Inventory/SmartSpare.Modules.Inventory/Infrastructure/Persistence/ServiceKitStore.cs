using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

/// <summary>The quantity <c>inventory.reserve_service_kit</c> holds for one service order line.</summary>
internal sealed record ReservedLine(long ServiceOrderLineId, decimal ReservedQty);

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
}
