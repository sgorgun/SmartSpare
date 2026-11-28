using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;

namespace SmartSpare.Modules.Fleet.Infrastructure.Queries;

/// <summary>The hub of every machine and service order, open or not (ADR 0029), for <c>fleet.hub-reference-publish</c>.</summary>
internal static class HubReferenceRows
{
    public static async Task<IReadOnlyList<HubReference>> AllAsync(FleetDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(
                """
                SELECT 'machine' AS record_kind, m.id AS record_id, m.home_warehouse_id AS warehouse_id, m.status = 'active' AS is_open
                FROM fleet.machine m
                UNION ALL
                SELECT 'service_order', o.id, o.warehouse_id, o.status IN ('draft', 'parts_reserved', 'in_progress')
                FROM fleet.service_order o
                ORDER BY 1, 2
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new HubReference(r.RecordKind, r.RecordId, r.WarehouseId, r.IsOpen))];
    }

    internal sealed record Row(string RecordKind, long RecordId, short WarehouseId, bool IsOpen);
}
