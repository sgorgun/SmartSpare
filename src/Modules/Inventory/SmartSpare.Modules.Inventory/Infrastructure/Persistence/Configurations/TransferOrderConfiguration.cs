using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.transfer_order</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class TransferOrderConfiguration : IEntityTypeConfiguration<TransferOrder>
{
    public void Configure(EntityTypeBuilder<TransferOrder> builder)
    {
        builder.ToTable("transfer_order", "inventory", t =>
        {
            t.HasComment("Inter-warehouse transfer order (TR-YYYY-NNNNNN). Status changes only through inventory.approve/dispatch/receive/cancel_transfer().");
            t.HasCheckConstraint("ck_transfer_order_approved", "((status = ANY (ARRAY['proposed'::inventory.transfer_status, 'cancelled'::inventory.transfer_status])) OR (approved_at IS NOT NULL))");
            t.HasCheckConstraint("ck_transfer_order_cancel", "((status = 'cancelled'::inventory.transfer_status) = (cancel_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_transfer_order_dispatched", "((status <> ALL (ARRAY['dispatched'::inventory.transfer_status, 'received'::inventory.transfer_status])) OR (dispatched_at IS NOT NULL))");
            t.HasCheckConstraint("ck_transfer_order_distinct", "(from_warehouse_id <> to_warehouse_id)");
            t.HasCheckConstraint("ck_transfer_order_number", "((number)::text ~ '^TR-\\d{4}-\\d{6}$'::text)");
            t.HasCheckConstraint("ck_transfer_order_received", "((status = 'received'::inventory.transfer_status) = (received_at IS NOT NULL))");
            t.HasCheckConstraint("ck_transfer_order_timeline", "((dispatched_at IS NULL) OR (dispatched_at >= approved_at))");
            t.HasCheckConstraint("ck_transfer_order_timeline2", "((received_at IS NULL) OR (received_at >= dispatched_at))");
        });

        builder.HasKey(x => x.Id).HasName("pk_transfer_order");
        builder.HasAlternateKey(x => x.Number).HasName("uq_transfer_order_number");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Number).HasColumnName("number").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Business document number (assigned by trigger).");
        builder.Property(x => x.FromWarehouseId).HasColumnName("from_warehouse_id").HasColumnOrder(2).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_route.");
        builder.Property(x => x.ToWarehouseId).HasColumnName("to_warehouse_id").HasColumnOrder(3).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_route.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'proposed'::inventory.transfer_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.IsUrgent).HasColumnName("is_urgent").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("Flag: urgent.");
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasColumnOrder(6).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasColumnOrder(7).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasComment("Time of the approved event.");
        builder.Property(x => x.DispatchedBy).HasColumnName("dispatched_by").HasColumnOrder(9).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.DispatchedAt).HasColumnName("dispatched_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasComment("Time of the dispatched event.");
        builder.Property(x => x.ReceivedBy).HasColumnName("received_by").HasColumnOrder(11).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnOrder(12).HasColumnType("timestamp with time zone").HasComment("Time of the received event.");
        builder.Property(x => x.CancelReason).HasColumnName("cancel_reason").HasColumnOrder(13).HasColumnType("character varying(300)");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(14).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(15).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_transfer_order_approved_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_transfer_order_dispatched_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_transfer_order_received_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_transfer_order_requested_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<WarehouseRoute>().WithMany()
            .HasForeignKey(x => new { x.FromWarehouseId, x.ToWarehouseId })
            .HasPrincipalKey(x => new { x.FromWarehouseId, x.ToWarehouseId })
            .HasConstraintName("fk_transfer_order_route").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.ApprovedBy).HasDatabaseName("ix_transfer_order_approved_by");
        builder.HasIndex(x => x.DispatchedBy).HasDatabaseName("ix_transfer_order_dispatched_by");
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_transfer_order_open").HasFilter("(status = ANY (ARRAY['proposed'::inventory.transfer_status, 'approved'::inventory.transfer_status, 'dispatched'::inventory.transfer_status]))");
        builder.HasIndex(x => x.ReceivedBy).HasDatabaseName("ix_transfer_order_received_by");
        builder.HasIndex(x => x.RequestedBy).HasDatabaseName("ix_transfer_order_requested_by");
        builder.HasIndex(x => x.ToWarehouseId).HasDatabaseName("ix_transfer_order_to_wh");
    }
}
