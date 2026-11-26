using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Features.PartCatalog;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The initial fill of the consumers' catalogue copies (STAGE_6_PLAN 5.2): the job writes the whole catalogue to the
/// outbox as <c>PartCatalogChanged</c> chunks.
/// </summary>
[Trait("Category", "Integration")]
[Collection(InventoryDatabaseGroup.Name)]
public sealed class PartCatalogPublishJobTests(InventoryDatabaseFixture database)
{
    private const string PublishedParts =
        "SELECT coalesce(sum(jsonb_array_length(payload -> 'parts')), 0)::bigint FROM inventory.outbox_message WHERE type LIKE '%.PartCatalogChanged,%'";

    [Fact]
    public async Task The_job_publishes_every_part_of_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        await database.ScalarAsync<int>(
            """
            WITH ins AS (
                INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
                SELECT b.id, 'PUB' || g, 'Published part', c.id, u.id, 1
                FROM generate_series(1, 3) g, ref.brand b, ref.part_category c, ref.unit_of_measure u
                WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
                ON CONFLICT DO NOTHING RETURNING 1)
            SELECT count(*)::int FROM ins
            """);
        var parts = await database.ScalarAsync<long>("SELECT count(*) FROM catalog.part");
        var before = await database.ScalarAsync<long>(PublishedParts);

        await using (var context = database.CreateContext())
        {
            await new PartCatalogPublishJob(context, new EfUnitOfWork<Infrastructure.Persistence.InventoryDbContext>(context)).RunAsync(ct);
        }

        (await database.ScalarAsync<long>(PublishedParts) - before).ShouldBe(parts);
        new PartCatalogPublishJob(null!, null!).Name.ShouldBe("inventory.part-catalog-publish");
    }
}
