using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Identity.Infrastructure.Messaging;

/// <summary>
/// Keeps <c>iam.warehouse_snapshot</c> in step with Inventory's warehouses (ADR 0029, PR 7j1). Runs in Identity's
/// transaction with the inbox row, so a redelivered event is skipped; <c>iam.apply_warehouses</c>, the copy's only write
/// path, keeps the row of the newer event, so a late or replayed event never overwrites newer data.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class WarehouseCatalogChangedConsumer(IdentityDbContext db) : IIntegrationEventConsumer<WarehouseCatalogChanged>
{
    public const string Name = "identity.warehouse-snapshot";

    public Task Handle(WarehouseCatalogChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.Warehouses.Count == 0
            ? Task.CompletedTask
            : db.Database.ExecuteSqlRawAsync("SELECT iam.apply_warehouses(@occurred_at, @ids, @codes, @names, @central, @archived)",
            [
                new NpgsqlParameter<short[]>("ids", [.. integrationEvent.Warehouses.Select(w => w.WarehouseId)]),
                new NpgsqlParameter<string[]>("codes", [.. integrationEvent.Warehouses.Select(w => w.Code)]),
                new NpgsqlParameter<string[]>("names", [.. integrationEvent.Warehouses.Select(w => w.Name)]),
                new NpgsqlParameter<bool[]>("central", [.. integrationEvent.Warehouses.Select(w => w.IsCentralHub)]),
                new NpgsqlParameter<bool[]>("archived", [.. integrationEvent.Warehouses.Select(w => w.IsArchived)]),
                new NpgsqlParameter<DateTimeOffset>("occurred_at", integrationEvent.OccurredAt),
            ],
            cancellationToken);
    }
}
