using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.transfer_order_line</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class TransferOrderLineConfiguration : IEntityTypeConfiguration<TransferOrderLine>
{
    public void Configure(EntityTypeBuilder<TransferOrderLine> builder)
    {
        builder.ToTable("transfer_order_line", "inventory", t =>
        {
            t.HasComment("Parts and quantities of a transfer; proposed_qty keeps the system proposal for KPI-04 (acceptance rate).");
            t.HasCheckConstraint("ck_transfer_order_line_override", "((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_transfer_order_line_proposed", "((proposed_qty IS NULL) OR (proposed_qty > (0)::numeric))");
            t.HasCheckConstraint("ck_transfer_order_line_qty", "(quantity > (0)::numeric)");
        });

        builder.HasKey(x => x.Id).HasName("pk_transfer_order_line");
        builder.HasAlternateKey(x => new { x.TransferOrderId, x.PartId }).HasName("uq_transfer_order_line_part");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.TransferOrderId).HasColumnName("transfer_order_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to inventory.transfer_order.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.ProposedQty).HasColumnName("proposed_qty").HasColumnOrder(4).HasColumnType("numeric(12,3)").HasComment("Quantity proposed by the system; differs from quantity only with override_reason (KPI-04).");
        builder.Property(x => x.OverrideReason).HasColumnName("override_reason").HasColumnOrder(5).HasColumnType("character varying(300)").HasComment("Mandatory justification of a manual change (UC01 A2).");

        builder.HasOne<TransferOrder>().WithMany()
            .HasForeignKey(x => x.TransferOrderId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_transfer_order_line_order").OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Part>().WithMany()
            .HasForeignKey(x => x.PartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_transfer_order_line_part").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_transfer_order_line_part");
    }
}
