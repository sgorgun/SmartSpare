using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Messaging;

/// <summary>
/// Keeps <c>inventory.fleet_hub_reference</c> in step with Fleet's service orders and machines (ADR 0029, PR 7j1), so the
/// warehouse archive guard sees what Fleet holds at a hub. Runs in Inventory's transaction with the inbox row, so a
/// redelivered event is skipped; <c>inventory.apply_fleet_hub_references</c>, the projection's only write path, keeps the
/// row of the newer event per record, so a late or replayed event never overwrites newer state.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class FleetHubReferencesConsumer(InventoryDbContext db) : IIntegrationEventConsumer<HubReferencesChanged>
{
    public const string Name = "inventory.fleet-hub-references";

    public Task Handle(HubReferencesChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.References.Count == 0
            ? Task.CompletedTask
            : db.Database.ExecuteSqlRawAsync("SELECT inventory.apply_fleet_hub_references(@occurred_at, @kinds, @ids, @warehouses, @open)",
            [
                new NpgsqlParameter<string[]>("kinds", [.. integrationEvent.References.Select(r => r.RecordKind)]),
                new NpgsqlParameter<long[]>("ids", [.. integrationEvent.References.Select(r => r.RecordId)]),
                new NpgsqlParameter<short[]>("warehouses", [.. integrationEvent.References.Select(r => r.WarehouseId)]),
                new NpgsqlParameter<bool[]>("open", [.. integrationEvent.References.Select(r => r.IsOpen)]),
                new NpgsqlParameter<DateTimeOffset>("occurred_at", integrationEvent.OccurredAt),
            ],
            cancellationToken);
    }
}
