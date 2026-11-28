using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>fleet.warehouse_snapshot</c> (ADR 0029, PR 7j1): Fleet's read-only copy of Inventory's warehouses with the same ids,
/// for hub codes and the archived-hub guard of service orders and machines. Grants, the write function and the guard
/// triggers: <c>db/sql/fleet/0012_warehouse_snapshot.sql</c>.
/// </summary>
internal sealed class WarehouseSnapshotConfiguration : IEntityTypeConfiguration<WarehouseSnapshot>
{
    public void Configure(EntityTypeBuilder<WarehouseSnapshot> builder)
    {
        builder.ToTable("warehouse_snapshot", "fleet", t =>
            t.HasComment("Read-only copy of the Inventory warehouses, fed by WarehouseCatalogChanged. Written only by fleet.apply_warehouses."));

        builder.HasKey(x => x.WarehouseId).HasName("pk_warehouse_snapshot");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(0).HasColumnType("smallint").ValueGeneratedNever().HasComment("Inventory warehouse id (inventory.warehouse.id); no cross-module FK.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Warehouse code, e.g. KLP.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(120)").HasComment("Warehouse name.");
        builder.Property(x => x.IsCentralHub).HasColumnName("is_central_hub").HasColumnOrder(3).HasColumnType("boolean").HasComment("TRUE = the central hub.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(4).HasColumnType("boolean").HasComment("TRUE = archived in Inventory; no new service order or home hub may use it (SS500).");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");
    }
}
