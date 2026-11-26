using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Fleet.Infrastructure.Messaging;

/// <summary>
/// Keeps <c>fleet.part_snapshot</c> in step with the Inventory catalogue (STAGE_6_PLAN section 5.2, #12). The delivery
/// pipeline runs it in Fleet's transaction with the inbox row, so a redelivered event is skipped; the upsert keeps the
/// row of the newer event, so a late or replayed chunk never overwrites newer data. Pure data access, so it lives in
/// Infrastructure.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class PartCatalogChangedConsumer(FleetDbContext db) : IIntegrationEventConsumer<PartCatalogChanged>
{
    public const string Name = "fleet.part-snapshot";

    private const string UpsertSql =
        """
        INSERT INTO fleet.part_snapshot AS s (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at, updated_at)
        SELECT p.part_id, p.brand_code, p.oem_part_number, p.description, p.unit_of_measure, p.is_active, @occurred_at, now()
        FROM unnest(@part_ids, @brand_codes, @oem_numbers, @descriptions, @units, @active)
             AS p (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active)
        ON CONFLICT (part_id) DO UPDATE
           SET brand_code = EXCLUDED.brand_code, oem_part_number = EXCLUDED.oem_part_number,
               description = EXCLUDED.description, unit_of_measure = EXCLUDED.unit_of_measure,
               is_active = EXCLUDED.is_active, source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
         WHERE s.source_occurred_at < EXCLUDED.source_occurred_at
        """;

    public Task Handle(PartCatalogChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var parts = integrationEvent.Parts;
        if (parts.Count == 0)
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                new NpgsqlParameter("part_ids", parts.Select(p => p.PartId).ToArray()),
                new NpgsqlParameter("brand_codes", parts.Select(p => p.BrandCode).ToArray()),
                new NpgsqlParameter("oem_numbers", parts.Select(p => p.OemPartNumber).ToArray()),
                new NpgsqlParameter("descriptions", parts.Select(p => p.Description).ToArray()),
                new NpgsqlParameter("units", parts.Select(p => p.UnitOfMeasure).ToArray()),
                new NpgsqlParameter("active", parts.Select(p => p.IsActive).ToArray()),
                new NpgsqlParameter("occurred_at", integrationEvent.OccurredAt),
            ],
            cancellationToken);
    }
}
