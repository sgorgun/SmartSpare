using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// Fleet's copy of the catalogue (STAGE_6_PLAN 5.2): the consumer of <see cref="PartCatalogChanged"/> inserts and updates
/// <c>fleet.part_snapshot</c> and never lets an older event overwrite a newer one. Redelivery of the same event is
/// stopped earlier by the inbox (IntegrationEventDelivery).
/// </summary>
[Trait("Category", "Integration")]
[Collection(FleetDemoDatabaseGroup.Name)]
public sealed class PartSnapshotConsumerTests(FleetDemoDatabaseFixture database)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_parts_are_copied_and_newer_events_win_over_older_ones()
    {
        const long partId = 9_000_001;
        await HandleAsync(new PartCatalogChanged([new PartSnapshot(partId, "JD", "RE900901", "Oil filter", "PCS", true)]) { OccurredAt = T0 });
        await HandleAsync(new PartCatalogChanged([new PartSnapshot(partId, "JD", "RE900901", "Oil filter, long life", "PCS", false)]) { OccurredAt = T0.AddMinutes(5) });
        await HandleAsync(new PartCatalogChanged([new PartSnapshot(partId, "JD", "RE900901", "Stale description", "PCS", true)]) { OccurredAt = T0.AddMinutes(1) });

        await using var db = database.CreateContext();
        var row = await db.Set<Infrastructure.Persistence.PartSnapshot>().AsNoTracking().SingleAsync(p => p.PartId == partId, Ct);
        (row.BrandCode, row.OemPartNumber, row.Description, row.UnitOfMeasure, row.IsActive, row.SourceOccurredAt)
            .ShouldBe(("JD", "RE900901", "Oil filter, long life", "PCS", false, T0.AddMinutes(5)));
    }

    [Fact]
    public async Task An_empty_event_changes_nothing_and_the_demo_catalogue_is_copied()
    {
        await HandleAsync(new PartCatalogChanged([]));

        await using var db = database.CreateContext();
        (await db.Set<Infrastructure.Persistence.PartSnapshot>().CountAsync(p => p.PartId < 9_000_000, Ct)).ShouldBeGreaterThan(200);
    }

    private async Task HandleAsync(PartCatalogChanged integrationEvent)
    {
        await using var db = database.CreateContext();
        await new PartCatalogChangedConsumer(db).Handle(integrationEvent, Ct);
    }
}
