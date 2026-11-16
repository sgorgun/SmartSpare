using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>staging.part_catalog</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PartCatalogConfiguration : IEntityTypeConfiguration<PartCatalog>
{
    public void Configure(EntityTypeBuilder<PartCatalog> builder)
    {
        builder.ToTable("part_catalog", "staging", t =>
        {
            t.HasComment("Staging layer: typed and validated part catalogue rows (valid rows are upserted into catalog.part).");
            t.HasCheckConstraint("ck_staging_part_catalog_valid", "((NOT is_valid) OR ((brand_id IS NOT NULL) AND (oem_part_number IS NOT NULL) AND (description IS NOT NULL) AND (category_id IS NOT NULL) AND (uom_id IS NOT NULL) AND (unit_cost IS NOT NULL)))");
        });

        builder.HasKey(x => new { x.ImportBatchId, x.LineNo }).HasName("pk_staging_part_catalog");

        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to raw.part_catalog_row.");
        builder.Property(x => x.LineNo).HasColumnName("line_no").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to raw.part_catalog_row.");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(2).HasColumnType("smallint");
        builder.Property(x => x.OemPartNumber).HasColumnName("oem_part_number").HasColumnOrder(3).HasColumnType("character varying(40)");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(4).HasColumnType("character varying(300)").HasComment("Description.");
        builder.Property(x => x.CategoryId).HasColumnName("category_id").HasColumnOrder(5).HasColumnType("integer");
        builder.Property(x => x.UomId).HasColumnName("uom_id").HasColumnOrder(6).HasColumnType("smallint");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasColumnOrder(7).HasColumnType("numeric(9,3)");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnOrder(8).HasColumnType("numeric(12,2)");
        builder.Property(x => x.IsConditionBased).HasColumnName("is_condition_based").HasColumnOrder(9).HasColumnType("boolean").HasComment("Flag: condition based.");
        builder.Property(x => x.IsValid).HasColumnName("is_valid").HasColumnOrder(10).HasColumnType("boolean").HasComment("Flag: valid.");

        builder.HasOne<PartCatalogRow>().WithMany()
            .HasForeignKey(x => new { x.ImportBatchId, x.LineNo })
            .HasPrincipalKey(x => new { x.ImportBatchId, x.LineNo })
            .HasConstraintName("fk_staging_part_catalog_raw").OnDelete(DeleteBehavior.Cascade);
    }
}
