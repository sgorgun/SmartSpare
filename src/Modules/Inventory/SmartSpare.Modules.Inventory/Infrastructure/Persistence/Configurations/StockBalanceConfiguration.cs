using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.stock_balance</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("stock_balance", "inventory", t =>
        {
            t.HasComment("Current stock per warehouse and part. Controlled redundancy: = SUM of the ledger, kept in sync by triggers for O(1) reads (NFR01).");
            t.HasCheckConstraint("ck_stock_balance_on_hand_non_negative", "(on_hand_qty >= (0)::numeric)");
            t.HasCheckConstraint("ck_stock_balance_reserved_le_on_hand", "(reserved_qty <= on_hand_qty)");
            t.HasCheckConstraint("ck_stock_balance_reserved_non_negative", "(reserved_qty >= (0)::numeric)");
        });

        builder.HasKey(x => new { x.WarehouseId, x.PartId }).HasName("pk_stock_balance");

        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(0).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.OnHandQty).HasColumnName("on_hand_qty").HasColumnOrder(2).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Physical quantity in the warehouse (SUM of ledger movements); never negative.");
        builder.Property(x => x.ReservedQty).HasColumnName("reserved_qty").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Quantity held by active (soft/firm) reservations; never above on_hand_qty.");
        builder.Property(x => x.AvailableQty).HasColumnName("available_qty").HasColumnOrder(4).HasColumnType("numeric(12,3)").HasComputedColumnSql("(on_hand_qty - reserved_qty)", stored: true).HasComment("Generated column: on_hand - reserved (never stored inconsistently).");
        builder.Property(x => x.LastMovementAt).HasColumnName("last_movement_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasComment("Time of the latest physical movement (US04 \"last updated\").");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");

        builder.HasOne<WarehousePart>().WithMany()
            .HasForeignKey(x => new { x.WarehouseId, x.PartId })
            .HasPrincipalKey(x => new { x.WarehouseId, x.PartId })
            .HasConstraintName("fk_stock_balance_warehouse_part").OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_stock_balance_part");
    }
}
