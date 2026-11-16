using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Configurations;

/// <summary><c>procurement.purchase_order_line</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("purchase_order_line", "procurement", t =>
        {
            t.HasComment("PO line. MOQ / pack-size rules of procurement.supplier_part are enforced by trigger.");
            t.HasCheckConstraint("ck_purchase_order_line_override", "((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_purchase_order_line_price", "(unit_price > (0)::numeric)");
            t.HasCheckConstraint("ck_purchase_order_line_qty", "(quantity > (0)::numeric)");
            t.HasCheckConstraint("ck_purchase_order_line_received", "((received_qty >= (0)::numeric) AND (received_qty <= quantity))");
        });

        builder.HasKey(x => x.Id).HasName("pk_purchase_order_line");
        builder.HasAlternateKey(x => new { x.PurchaseOrderId, x.PartId }).HasName("uq_purchase_order_line_part");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to procurement.purchase_order.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasColumnOrder(4).HasColumnType("numeric(12,2)");
        builder.Property(x => x.LineAmount).HasColumnName("line_amount").HasColumnOrder(5).HasColumnType("numeric(14,2)").HasComputedColumnSql("round((quantity * unit_price), 2)", stored: true).HasComment("Generated column: round(quantity x unit_price, 2).");
        builder.Property(x => x.ReceivedQty).HasColumnName("received_qty").HasColumnOrder(6).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Quantity received so far (partial receipts).");
        builder.Property(x => x.ProposedQty).HasColumnName("proposed_qty").HasColumnOrder(7).HasColumnType("numeric(12,3)");
        builder.Property(x => x.OverrideReason).HasColumnName("override_reason").HasColumnOrder(8).HasColumnType("character varying(300)");
        // fk_purchase_order_line_part -> catalog.part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<PurchaseOrder>().WithMany()
            .HasForeignKey(x => x.PurchaseOrderId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_purchase_order_line_order").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_purchase_order_line_part");
    }
}
