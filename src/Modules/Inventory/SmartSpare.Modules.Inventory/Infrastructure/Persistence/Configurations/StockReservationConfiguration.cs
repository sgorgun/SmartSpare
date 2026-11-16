using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.stock_reservation</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable("stock_reservation", "inventory", t =>
        {
            t.HasComment("Stock holds. Active (soft/firm) quantities are summed into stock_balance.reserved_qty by trigger.");
            t.HasCheckConstraint("ck_stock_reservation_closed", "((status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status])) = (closed_at IS NULL))");
            t.HasCheckConstraint("ck_stock_reservation_one_source", "(num_nonnulls(service_order_line_id, transfer_order_line_id) = 1)");
            t.HasCheckConstraint("ck_stock_reservation_quantity", "(quantity > (0)::numeric)");
            t.HasCheckConstraint("ck_stock_reservation_soft_expires", "((status <> 'soft'::inventory.reservation_status) OR (expires_at IS NOT NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_stock_reservation");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to inventory.stock_balance.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to inventory.stock_balance.");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'soft'::inventory.reservation_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasComment("End of a soft hold (now + 7 days for service kits, UC02).");
        builder.Property(x => x.ServiceOrderLineId).HasColumnName("service_order_line_id").HasColumnOrder(6).HasColumnType("bigint").HasComment("Reference to fleet.service_order_line.");
        builder.Property(x => x.TransferOrderLineId).HasColumnName("transfer_order_line_id").HasColumnOrder(7).HasColumnType("bigint").HasComment("Reference to inventory.transfer_order_line.");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnOrder(8).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(9).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasComment("Time of the closed event.");
        // fk_stock_reservation_created_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_stock_reservation_service_line -> fleet.service_order_line removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<StockBalance>().WithMany()
            .HasForeignKey(x => new { x.WarehouseId, x.PartId })
            .HasPrincipalKey(x => new { x.WarehouseId, x.PartId })
            .HasConstraintName("fk_stock_reservation_balance").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TransferOrderLine>().WithMany()
            .HasForeignKey(x => x.TransferOrderLineId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_stock_reservation_transfer_line").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.WarehouseId, x.PartId }).HasDatabaseName("ix_stock_reservation_active").HasFilter("(status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status]))");
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_stock_reservation_created_by");
        builder.HasIndex(x => x.ServiceOrderLineId).HasDatabaseName("ix_stock_reservation_service_line").HasFilter("(service_order_line_id IS NOT NULL)");
        builder.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_stock_reservation_soft_expiry").HasFilter("(status = 'soft'::inventory.reservation_status)");
        builder.HasIndex(x => x.TransferOrderLineId).HasDatabaseName("ix_stock_reservation_transfer_line").HasFilter("(transfer_order_line_id IS NOT NULL)");
    }
}
