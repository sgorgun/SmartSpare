using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Configurations;

/// <summary><c>procurement.purchase_order</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_order", "procurement", t =>
        {
            t.HasComment("Supplier purchase order (PO-YYYY-NNNNNN). Drafts are generated from recommendations and approved by management (US14).");
            t.HasCheckConstraint("ck_purchase_order_approved", "((status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'cancelled'::procurement.po_status])) OR ((approved_by IS NOT NULL) AND (approved_at IS NOT NULL)))");
            t.HasCheckConstraint("ck_purchase_order_cancel", "((status = 'cancelled'::procurement.po_status) = (cancel_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_purchase_order_currency", "(currency_code ~ '^[A-Z]{3}$'::text)");
            t.HasCheckConstraint("ck_purchase_order_four_eyes", "((approved_by IS NULL) OR (total_amount <= (5000)::numeric) OR (approved_by <> created_by))");
            t.HasCheckConstraint("ck_purchase_order_number", "((number)::text ~ '^PO-\\d{4}-\\d{6}$'::text)");
            t.HasCheckConstraint("ck_purchase_order_timeline", "((approved_at IS NULL) OR (approved_at >= created_at))");
            t.HasCheckConstraint("ck_purchase_order_total", "(total_amount >= (0)::numeric)");
            t.HasCheckConstraint("ck_purchase_order_urgent", "((NOT is_urgent) OR (urgent_reason IS NOT NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_purchase_order");
        builder.HasAlternateKey(x => x.Number).HasName("uq_purchase_order_number");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Number).HasColumnName("number").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Business document number (assigned by trigger).");
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id").HasColumnOrder(2).HasColumnType("integer").HasComment("Reference to procurement.supplier.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(3).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'draft'::procurement.po_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.IsUrgent).HasColumnName("is_urgent").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("US16 emergency direct purchase (\"Urgent Harvest Dispatch\"), bypasses weekly batching.");
        builder.Property(x => x.UrgentReason).HasColumnName("urgent_reason").HasColumnOrder(6).HasColumnType("character varying(300)");
        builder.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasColumnOrder(7).HasColumnType("character(3)").HasDefaultValueSql("'EUR'::bpchar");
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount").HasColumnOrder(8).HasColumnType("numeric(14,2)").HasDefaultValueSql("0").HasComment("Controlled redundancy: SUM(line_amount), maintained by trigger; needed by the four-eyes CHECK.");
        builder.Property(x => x.ExpectedArrivalDate).HasColumnName("expected_arrival_date").HasColumnOrder(9).HasColumnType("date").HasComment("Approval date + longest supplier lead time of the lines.");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnOrder(10).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnOrder(12).HasColumnType("timestamp with time zone").HasComment("Time of the submitted event.");
        builder.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasColumnOrder(13).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at").HasColumnOrder(14).HasColumnType("timestamp with time zone").HasComment("Time of the approved event.");
        builder.Property(x => x.ExportedAt).HasColumnName("exported_at").HasColumnOrder(15).HasColumnType("timestamp with time zone").HasComment("Time of the exported event.");
        builder.Property(x => x.CancelReason).HasColumnName("cancel_reason").HasColumnOrder(16).HasColumnType("character varying(300)");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(17).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_purchase_order_approved_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_purchase_order_created_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_purchase_order_warehouse -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<Supplier>().WithMany()
            .HasForeignKey(x => x.SupplierId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_purchase_order_supplier").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.ApprovedBy).HasDatabaseName("ix_purchase_order_approved_by");
        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_purchase_order_created_by");
        builder.HasIndex(x => new { x.Status, x.ExpectedArrivalDate }).HasDatabaseName("ix_purchase_order_open").HasFilter("(status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'approved'::procurement.po_status, 'exported'::procurement.po_status, 'partially_received'::procurement.po_status]))");
        builder.HasIndex(x => new { x.SupplierId, x.CreatedAt }).HasDatabaseName("ix_purchase_order_supplier_created").IsDescending(false, true);
        builder.HasIndex(x => x.WarehouseId).HasDatabaseName("ix_purchase_order_warehouse");
    }
}
