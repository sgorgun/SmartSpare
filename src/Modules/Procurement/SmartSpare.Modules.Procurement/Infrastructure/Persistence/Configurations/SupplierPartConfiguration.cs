using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Configurations;

/// <summary><c>procurement.supplier_part</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class SupplierPartConfiguration : IEntityTypeConfiguration<SupplierPart>
{
    public void Configure(EntityTypeBuilder<SupplierPart> builder)
    {
        builder.ToTable("supplier_part", "procurement", t =>
        {
            t.HasComment("Commercial conditions of a supplier for a part: MOQ, pack size, lead time (US12).");
            t.HasCheckConstraint("ck_supplier_part_lead_time", "((lead_time_days >= 1) AND (lead_time_days <= 365))");
            t.HasCheckConstraint("ck_supplier_part_moq", "(moq > (0)::numeric)");
            t.HasCheckConstraint("ck_supplier_part_moq_pack", "(mod(moq, pack_size) = (0)::numeric)");
            t.HasCheckConstraint("ck_supplier_part_pack", "(pack_size > (0)::numeric)");
        });

        builder.HasKey(x => x.Id).HasName("pk_supplier_part");
        builder.HasAlternateKey(x => new { x.SupplierId, x.PartId }).HasName("uq_supplier_part");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to procurement.supplier.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.SupplierPartNumber).HasColumnName("supplier_part_number").HasColumnOrder(3).HasColumnType("character varying(40)");
        builder.Property(x => x.Moq).HasColumnName("moq").HasColumnOrder(4).HasColumnType("numeric(12,3)").HasDefaultValueSql("1").HasComment("Minimum order quantity; must be a multiple of pack_size.");
        builder.Property(x => x.PackSize).HasColumnName("pack_size").HasColumnOrder(5).HasColumnType("numeric(12,3)").HasDefaultValueSql("1").HasComment("Packaging increment; ordered quantities must be multiples of it.");
        builder.Property(x => x.LeadTimeDays).HasColumnName("lead_time_days").HasColumnOrder(6).HasColumnType("smallint").HasComment("Supplier lead time for this part (calendar days).");
        builder.Property(x => x.IsPreferred).HasColumnName("is_preferred").HasColumnOrder(7).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("Preferred supplier used for automatic PO drafts (max. one per part).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(9).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_supplier_part_part -> catalog.part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<Supplier>().WithMany()
            .HasForeignKey(x => x.SupplierId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_supplier_part_supplier").OnDelete(DeleteBehavior.Restrict);

        // Two indexes on part_id: named overloads, otherwise EF merges them into one.
        builder.HasIndex(x => x.PartId, "ix_supplier_part_part").HasDatabaseName("ix_supplier_part_part");
        builder.HasIndex(x => x.PartId, "uq_supplier_part_preferred").HasDatabaseName("uq_supplier_part_preferred").IsUnique().HasFilter("is_preferred");
    }
}
