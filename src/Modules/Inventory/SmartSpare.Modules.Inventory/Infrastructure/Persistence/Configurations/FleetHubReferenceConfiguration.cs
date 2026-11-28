using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>inventory.fleet_hub_reference</c> (ADR 0029, PR 7j1): Inventory's read-only projection of the Fleet records that hold a
/// hub, one row per record, so the warehouse archive guard (API and database) sees open service orders and machines based
/// at a hub without reading Fleet's schema. Grants and the write function: <c>db/sql/inventory/0025_fleet_hub_references.sql</c>.
/// </summary>
internal sealed class FleetHubReferenceConfiguration : IEntityTypeConfiguration<FleetHubReference>
{
    public void Configure(EntityTypeBuilder<FleetHubReference> builder)
    {
        builder.ToTable("fleet_hub_reference", "inventory", t =>
        {
            t.HasComment("Read-only projection of the Fleet records that hold a hub, fed by HubReferencesChanged. Written only by inventory.apply_fleet_hub_references.");
            t.HasCheckConstraint("ck_fleet_hub_reference_kind", "record_kind IN ('service_order', 'machine')");
        });

        builder.HasKey(x => new { x.RecordKind, x.RecordId }).HasName("pk_fleet_hub_reference");
        builder.Property(x => x.RecordKind).HasColumnName("record_kind").HasColumnOrder(0).HasColumnType("character varying(20)").HasComment("service_order or machine.");
        builder.Property(x => x.RecordId).HasColumnName("record_id").HasColumnOrder(1).HasColumnType("bigint").ValueGeneratedNever().HasComment("Fleet record id (fleet.service_order.id or fleet.machine.id); no cross-module FK.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(2).HasColumnType("smallint").HasComment("The hub the record holds: the order's hub or the machine's home hub.");
        builder.Property(x => x.IsOpen).HasColumnName("is_open").HasColumnOrder(3).HasColumnType("boolean").HasComment("TRUE = an open service order (draft, parts reserved, in progress) or an active machine.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(4).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");

        builder.HasIndex(x => x.WarehouseId).HasDatabaseName("ix_fleet_hub_reference_open").HasFilter("is_open");
    }
}
