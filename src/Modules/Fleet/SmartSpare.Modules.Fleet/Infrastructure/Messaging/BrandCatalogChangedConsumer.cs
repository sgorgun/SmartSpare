using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Infrastructure.Messaging;

/// <summary>
/// Keeps <c>fleet.brand_snapshot</c> in step with Inventory's brands (STAGE_7_PLAN 3.2, PR 7h1). Runs in Fleet's
/// transaction with the inbox row, so a redelivered event is skipped; <c>fleet.apply_brands</c>, the copy's only write
/// path, keeps the row of the newer event, so a late or replayed event never overwrites newer data.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class BrandCatalogChangedConsumer(FleetDbContext db) : IIntegrationEventConsumer<BrandCatalogChanged>
{
    public const string Name = "fleet.brand-snapshot";

    public Task Handle(BrandCatalogChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.Brands.Count == 0
            ? Task.CompletedTask
            : db.Database.ExecuteSqlRawAsync("SELECT fleet.apply_brands(@occurred_at, @ids, @codes, @names, @archived)",
            [
                new NpgsqlParameter<short[]>("ids", [.. integrationEvent.Brands.Select(b => b.BrandId)]),
                new NpgsqlParameter<string[]>("codes", [.. integrationEvent.Brands.Select(b => b.Code)]),
                new NpgsqlParameter<string[]>("names", [.. integrationEvent.Brands.Select(b => b.Name)]),
                new NpgsqlParameter<bool[]>("archived", [.. integrationEvent.Brands.Select(b => b.IsArchived)]),
                new NpgsqlParameter<DateTimeOffset>("occurred_at", integrationEvent.OccurredAt),
            ],
            cancellationToken);
    }
}
