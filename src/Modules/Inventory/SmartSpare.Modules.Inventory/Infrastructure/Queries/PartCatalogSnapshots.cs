using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

/// <summary>
/// Reads catalogue parts as the <see cref="PartSnapshot"/>s of <see cref="PartCatalogChanged"/> (STAGE_6_PLAN 5.2):
/// the parts an import changed, or the whole catalogue for the initial fill of a consumer's copy.
/// </summary>
internal static class PartCatalogSnapshots
{
    private const string Select =
        """
        SELECT p.id AS part_id, b.code AS brand_code, p.oem_part_number, p.description, u.code AS unit_of_measure, NOT p.is_archived AS is_active
        FROM catalog.part p
        JOIN catalog.brand b ON b.id = p.brand_id
        JOIN ref.unit_of_measure u ON u.id = p.uom_id

        """;

    /// <summary>
    /// The parts that <c>staging.load_part_catalog</c> inserted or changed in the current transaction: rows of the batch
    /// whose <c>created_at</c> or <c>updated_at</c> is the transaction time (<c>now()</c>, set by the default and by
    /// <c>trg_part_updated_at</c>). Rows the load left unchanged are not published again.
    /// </summary>
    public static async Task<IReadOnlyList<PartSnapshot>> ChangedByBatchAsync(InventoryDbContext db, long batchId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<PartSnapshot>(
                Select +
                """
                WHERE (p.created_at = now() OR p.updated_at = now())
                  AND EXISTS (SELECT 1 FROM staging.part_catalog s
                              WHERE s.import_batch_id = {0} AND s.is_valid
                                AND s.brand_id = p.brand_id AND s.oem_part_number = p.oem_part_number)
                ORDER BY p.id
                """, batchId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The given parts after a catalogue write (PR 7i1), read in the write's transaction.</summary>
    public static async Task<IReadOnlyList<PartSnapshot>> ByIdsAsync(InventoryDbContext db, IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<PartSnapshot>(Select + "WHERE p.id = ANY({0}) ORDER BY p.id", ids.ToArray())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The whole catalogue, in part id order.</summary>
    public static async Task<IReadOnlyList<PartSnapshot>> AllAsync(InventoryDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<PartSnapshot>(Select + "ORDER BY p.id").ToListAsync(cancellationToken).ConfigureAwait(false);
}
