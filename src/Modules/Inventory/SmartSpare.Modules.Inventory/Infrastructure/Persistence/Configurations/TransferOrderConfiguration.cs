using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Domain.Transfers;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>inventory.transfer_order</c> mapped onto the <see cref="TransferOrder"/> aggregate (names, types, constraints and
/// comments of V016 unchanged; ADR 0013). The number is assigned by the database trigger: EF Core gives a new order a
/// temporary number, leaves it out of the INSERT and reads the real one back.
/// </summary>
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
        builder.Property(x => x.Number).HasColumnName("number").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Business document number (assigned by trigger).")
            .ValueGeneratedOnAdd().HasValueGenerator<TemporaryDocumentNumberGenerator>();
        builder.Property(x => x.FromWarehouseId).HasColumnName("from_warehouse_id").HasColumnOrder(2).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_route.");
        builder.Property(x => x.ToWarehouseId).HasColumnName("to_warehouse_id").HasColumnOrder(3).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_route.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'proposed'::inventory.transfer_status").HasComment("Workflow status (state machine guarded by trigger).").ValueGeneratedNever();
        builder.Property(x => x.IsUrgent).HasColumnName("is_urgent").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("Flag: urgent.").ValueGeneratedNever();
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

        builder.HasMany(x => x.Lines).WithOne()
            .HasForeignKey(x => x.TransferOrderId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_transfer_order_line_order").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // PostgreSQL's xmin system column as the concurrency token (no DDL; STAGE_3_PLAN section 7).
        builder.Property<uint>("xmin").IsRowVersion();
        builder.Ignore(x => x.DomainEvents);
        builder.Ignore(x => x.PartIds);

        builder.HasIndex(x => x.ApprovedBy).HasDatabaseName("ix_transfer_order_approved_by");
        builder.HasIndex(x => x.DispatchedBy).HasDatabaseName("ix_transfer_order_dispatched_by");
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_transfer_order_open").HasFilter("(status = ANY (ARRAY['proposed'::inventory.transfer_status, 'approved'::inventory.transfer_status, 'dispatched'::inventory.transfer_status]))");
        builder.HasIndex(x => x.ReceivedBy).HasDatabaseName("ix_transfer_order_received_by");
        builder.HasIndex(x => x.RequestedBy).HasDatabaseName("ix_transfer_order_requested_by");
        builder.HasIndex(x => x.ToWarehouseId).HasDatabaseName("ix_transfer_order_to_wh");
    }
}
