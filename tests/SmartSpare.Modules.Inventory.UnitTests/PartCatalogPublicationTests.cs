using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Features.PartCatalog;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory;

/// <summary>Catalogue changes become chunked <see cref="PartCatalogChanged"/> events (STAGE_6_PLAN 5.2, ADR 0017 §5).</summary>
public sealed class PartCatalogPublicationTests
{
    private static PartSnapshot[] Parts(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new PartSnapshot(i, "JD", $"RE{i:000000}", "Filter", "PCS", IsActive: true))];

    [Fact]
    public void Parts_are_published_in_chunks_of_at_most_500()
    {
        var events = PartCatalogPublication.Events(Parts(1201));

        events.Select(e => e.Parts.Count).ShouldBe([500, 500, 201]);
        events.SelectMany(e => e.Parts).Select(p => p.PartId).ShouldBe(Enumerable.Range(1, 1201).Select(i => (long)i));
        events.Select(e => e.Id).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void No_changed_parts_publish_nothing()
    {
        PartCatalogPublication.Events([]).ShouldBeEmpty();
    }

    [Fact]
    public void The_events_are_queued_in_the_unit_of_work()
    {
        var unitOfWork = new RecordingUnitOfWork();

        PartCatalogPublication.Queue(unitOfWork, Parts(501));

        unitOfWork.Events.Select(e => ((PartCatalogChanged)e).Parts.Count).ShouldBe([500, 1]);
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public List<IIntegrationEvent> Events { get; } = [];

        public void AddIntegrationEvent(IIntegrationEvent integrationEvent) => Events.Add(integrationEvent);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
