using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.service_order_line</c> mapped onto <see cref="ServiceOrderLine"/> (V016 unchanged; ADR 0013).</summary>
internal sealed class ServiceOrderLineConfiguration : IEntityTypeConfiguration<ServiceOrderLine>
{
    public void Configure(EntityTypeBuilder<ServiceOrderLine> builder)
    {
        builder.ToTable("service_order_line", "fleet", t =>
        {
            t.HasComment("Parts required by a service order (copied from the kit, editable for repairs).");
            t.HasCheckConstraint("ck_service_order_line_issued", "((qty_issued >= (0)::numeric) AND (qty_issued <= qty_required))");
            t.HasCheckConstraint("ck_service_order_line_required", "(qty_required > (0)::numeric)");
            t.HasCheckConstraint("ck_service_order_line_reserved", "((qty_reserved >= (0)::numeric) AND (qty_reserved <= qty_required))");
        });

        builder.HasKey(x => x.Id).HasName("pk_service_order_line");
        builder.HasAlternateKey(x => new { x.ServiceOrderId, x.PartId }).HasName("uq_service_order_line_part");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ServiceOrderId).HasColumnName("service_order_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to fleet.service_order.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.QtyRequired).HasColumnName("qty_required").HasColumnOrder(3).HasColumnType("numeric(12,3)").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.QtyIssued).HasColumnName("qty_issued").HasColumnOrder(4).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Quantity already issued from stock (ISSUE_SERVICE).");
        builder.Property(x => x.QtyReserved).HasColumnName("qty_reserved").HasColumnOrder(5).HasColumnType("numeric(12,3)").HasDefaultValueSql("0").HasComment("Fleet's copy of the quantity Inventory holds for the line (ServiceKitReserved; MODULE_MAPPING 3.5).");
        // fk_service_order_line_part -> catalog.part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        // fk_service_order_line_order is configured with the Lines navigation of ServiceOrder.
        builder.Ignore(x => x.QtyOpen);
        builder.Ignore(x => x.QtyMissing);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_service_order_line_part");
    }
}
