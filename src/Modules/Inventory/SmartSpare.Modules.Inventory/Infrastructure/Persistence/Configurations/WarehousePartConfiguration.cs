using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.warehouse_part</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class WarehousePartConfiguration : IEntityTypeConfiguration<WarehousePart>
{
    public void Configure(EntityTypeBuilder<WarehousePart> builder)
    {
        builder.ToTable("warehouse_part", "inventory", t =>
        {
            t.HasComment("Stocking policy of a part in a warehouse: shelf location, safety stock, maximum.");
            t.HasCheckConstraint("ck_warehouse_part_bin", "((bin_location IS NULL) OR ((bin_location)::text ~ '^[A-Z0-9-]{2,20}$'::text))");
            t.HasCheckConstraint("ck_warehouse_part_max", "((max_stock_qty IS NULL) OR (max_stock_qty >= safety_stock_qty))");
            t.HasCheckConstraint("ck_warehouse_part_safety", "(safety_stock_qty >= (0)::numeric)");
        });

        builder.HasKey(x => new { x.WarehouseId, x.PartId }).HasName("pk_warehouse_part");

        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(0).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.BinLocation).HasColumnName("bin_location").HasColumnOrder(2).HasColumnType("character varying(20)");
        builder.Property(x => x.SafetyStockQty).HasColumnName("safety_stock_qty").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Local safety buffer; a donor hub may only give away stock above it (US10).");
        builder.Property(x => x.MaxStockQty).HasColumnName("max_stock_qty").HasColumnOrder(4).HasColumnType("numeric(12,3)");
        builder.Property(x => x.IsStocked).HasColumnName("is_stocked").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("Flag: stocked.").HasSentinel(true);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");

        // PostgreSQL's xmin system column as the concurrency token (no DDL; STAGE_3_PLAN section 7).
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<Domain.Catalog.Part>().WithMany()
            .HasForeignKey(x => x.PartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_warehouse_part_part").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(x => x.WarehouseId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_warehouse_part_warehouse").OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_warehouse_part_part");
    }
}
