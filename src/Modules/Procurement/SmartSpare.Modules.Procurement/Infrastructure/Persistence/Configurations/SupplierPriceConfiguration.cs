using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Configurations;

/// <summary><c>procurement.supplier_price</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class SupplierPriceConfiguration : IEntityTypeConfiguration<SupplierPrice>
{
    public void Configure(EntityTypeBuilder<SupplierPrice> builder)
    {
        builder.ToTable("supplier_price", "procurement", t =>
        {
            t.HasComment("Supplier purchase price history; non-overlapping validity periods per supplier-part (EXCLUDE USING gist).");
            t.HasCheckConstraint("ck_supplier_price_amount", "(unit_price > (0)::numeric)");
            t.HasCheckConstraint("ck_supplier_price_range", "((NOT isempty(valid_during)) AND (lower(valid_during) IS NOT NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_supplier_price");
        // ex_supplier_price_no_overlap (EXCLUDE USING gist (supplier_part_id WITH =, valid_during WITH &&)) is an EXCLUDE constraint; created in SQL.

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.SupplierPartId).HasColumnName("supplier_part_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to procurement.supplier_part.");
        builder.Property(x => x.ValidDuring).HasColumnName("valid_during").HasColumnOrder(2).HasColumnType("daterange").HasComment("Validity period [from, to); open upper bound = valid until further notice.");
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasColumnOrder(3).HasColumnType("numeric(12,2)");

        builder.HasOne<SupplierPart>().WithMany()
            .HasForeignKey(x => x.SupplierPartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_supplier_price_supplier_part").OnDelete(DeleteBehavior.Cascade);
    }
}
