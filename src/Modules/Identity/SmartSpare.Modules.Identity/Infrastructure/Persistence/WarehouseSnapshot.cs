using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>Read-only copy of an Inventory warehouse (ADR 0029, PR 7j1), written only through <c>iam.apply_warehouses</c>.</summary>
internal sealed class WarehouseSnapshot
{
    public short WarehouseId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsCentralHub { get; set; }

    public bool IsArchived { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// <c>iam.warehouse_snapshot</c>: Identity's copy of Inventory's warehouses with the same ids, for the hub codes in the user
/// list and the active hubs in the user form. Hub access itself is by id and does not read it. Grants and the write
/// function: <c>db/sql/identity/0020_warehouse_snapshot.sql</c>.
/// </summary>
internal sealed class WarehouseSnapshotConfiguration : IEntityTypeConfiguration<WarehouseSnapshot>
{
    public void Configure(EntityTypeBuilder<WarehouseSnapshot> builder)
    {
        builder.ToTable("warehouse_snapshot", "iam", t =>
            t.HasComment("Read-only copy of the Inventory warehouses, fed by WarehouseCatalogChanged. Written only by iam.apply_warehouses."));

        builder.HasKey(x => x.WarehouseId).HasName("pk_warehouse_snapshot");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(0).HasColumnType("smallint").ValueGeneratedNever().HasComment("Inventory warehouse id (inventory.warehouse.id); no cross-module FK.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Warehouse code, e.g. KLP.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(120)").HasComment("Warehouse name.");
        builder.Property(x => x.IsCentralHub).HasColumnName("is_central_hub").HasColumnOrder(3).HasColumnType("boolean").HasComment("TRUE = the central hub.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(4).HasColumnType("boolean").HasComment("TRUE = archived in Inventory; not offered in the user form.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");
    }
}
