using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.stock_movement</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        // Partitioned table: created with its partitions, indexes, FKs and triggers in SQL
        // (db/sql/inventory/0001_stock_movement.sql); mapped here for reads and inserts only.
        builder.ToTable("stock_movement", "inventory", t =>
        {
            t.ExcludeFromMigrations();
        });

        builder.HasKey(x => new { x.Id, x.OccurredAt }).HasName("pk_stock_movement");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnOrder(1).HasColumnType("timestamp with time zone");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(2).HasColumnType("smallint");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(3).HasColumnType("bigint");
        builder.Property(x => x.MovementTypeCode).HasColumnName("movement_type_code").HasColumnOrder(4).HasColumnType("character varying(20)");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(5).HasColumnType("numeric(12,3)");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnOrder(6).HasColumnType("numeric(12,2)");
        builder.Property(x => x.ServiceOrderLineId).HasColumnName("service_order_line_id").HasColumnOrder(7).HasColumnType("bigint");
        builder.Property(x => x.TransferOrderLineId).HasColumnName("transfer_order_line_id").HasColumnOrder(8).HasColumnType("bigint");
        builder.Property(x => x.PurchaseOrderLineId).HasColumnName("purchase_order_line_id").HasColumnOrder(9).HasColumnType("bigint");
        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(10).HasColumnType("bigint");
        builder.Property(x => x.ExternalDocRef).HasColumnName("external_doc_ref").HasColumnOrder(11).HasColumnType("character varying(40)");
        builder.Property(x => x.Note).HasColumnName("note").HasColumnOrder(12).HasColumnType("character varying(300)");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnOrder(13).HasColumnType("uuid");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(14).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
    }
}
