using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>catalog.part</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public void Configure(EntityTypeBuilder<Part> builder)
    {
        builder.ToTable("part", "catalog", t =>
        {
            t.HasComment("Spare part (SKU) catalogue; natural key = brand + OEM part number.");
            t.HasCheckConstraint("ck_part_description", "(length(btrim((description)::text)) >= 3)");
            t.HasCheckConstraint("ck_part_oem_number", "((oem_part_number)::text ~ '^[A-Z0-9][A-Z0-9.\\-/]{2,39}$'::text)");
            t.HasCheckConstraint("ck_part_unit_cost", "(unit_cost >= (0)::numeric)");
            t.HasCheckConstraint("ck_part_weight", "((weight_kg IS NULL) OR (weight_kg >= (0)::numeric))");
        });

        builder.HasKey(x => x.Id).HasName("pk_part");
        builder.HasAlternateKey(x => new { x.BrandId, x.OemPartNumber }).HasName("uq_part_brand_oem");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to catalog.brand.");
        builder.Property(x => x.OemPartNumber).HasColumnName("oem_part_number").HasColumnOrder(2).HasColumnType("character varying(40)").HasComment("Manufacturer part number, upper-case, normalised during import.");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(3).HasColumnType("character varying(300)").HasComment("Description.");
        builder.Property(x => x.CategoryId).HasColumnName("category_id").HasColumnOrder(4).HasColumnType("integer").HasComment("Reference to ref.part_category.");
        builder.Property(x => x.UomId).HasColumnName("uom_id").HasColumnOrder(5).HasColumnType("smallint").HasComment("Reference to ref.unit_of_measure.");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasColumnOrder(6).HasColumnType("numeric(9,3)");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnOrder(7).HasColumnType("numeric(12,2)").HasComment("Standard (valuation) cost per unit, EUR. Money is numeric, never float.");
        builder.Property(x => x.IsConditionBased).HasColumnName("is_condition_based").HasColumnOrder(8).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("TRUE for parts replaced \"as required\" (air/cab filters, finding F6): demand only from the forecast stream.").ValueGeneratedNever();
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(9).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");

        builder.Property(x => x.ManufacturerId).HasColumnName("manufacturer_id").HasColumnOrder(12).HasColumnType("smallint")
            .HasComment("Maker of the part (catalog.manufacturer), if known.");
        builder.HasOne<Domain.Catalog.Manufacturer>().WithMany().HasForeignKey(x => x.ManufacturerId).HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_manufacturer").OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(x => x.ManufacturerId).HasDatabaseName("ix_part_manufacturer");
        builder.HasOne<Domain.Catalog.Brand>().WithMany()
            .HasForeignKey(x => x.BrandId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_brand").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<RefPartCategory>().WithMany()
            .HasForeignKey(x => x.CategoryId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_category").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<RefUnitOfMeasure>().WithMany()
            .HasForeignKey(x => x.UomId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_uom").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.CategoryId).HasDatabaseName("ix_part_category");
        builder.HasIndex(x => x.Description).HasDatabaseName("ix_part_description_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.HasIndex(x => x.OemPartNumber).HasDatabaseName("ix_part_oem_number_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.HasIndex(x => x.UomId).HasDatabaseName("ix_part_uom");
    }
}
