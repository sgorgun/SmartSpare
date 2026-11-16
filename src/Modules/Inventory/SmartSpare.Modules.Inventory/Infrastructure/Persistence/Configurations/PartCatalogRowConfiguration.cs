using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>raw.part_catalog_row</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PartCatalogRowConfiguration : IEntityTypeConfiguration<PartCatalogRow>
{
    public void Configure(EntityTypeBuilder<PartCatalogRow> builder)
    {
        builder.ToTable("part_catalog_row", "raw", t =>
        {
            t.HasComment("Raw layer: part catalogue CSV rows as text (columns: brand, oem_part_number, description, category, uom, weight_kg, unit_cost, condition_based).");
            t.HasCheckConstraint("ck_part_catalog_row_line", "(line_no > 0)");
        });

        builder.HasKey(x => new { x.ImportBatchId, x.LineNo }).HasName("pk_part_catalog_row");

        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to raw.import_batch.");
        builder.Property(x => x.LineNo).HasColumnName("line_no").HasColumnOrder(1).HasColumnType("integer").HasComment("Line number in the uploaded file.");
        builder.Property(x => x.BrandCode).HasColumnName("brand_code").HasColumnOrder(2).HasColumnType("text");
        builder.Property(x => x.OemPartNumber).HasColumnName("oem_part_number").HasColumnOrder(3).HasColumnType("text");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(4).HasColumnType("text").HasComment("Description.");
        builder.Property(x => x.CategoryCode).HasColumnName("category_code").HasColumnOrder(5).HasColumnType("text");
        builder.Property(x => x.UomCode).HasColumnName("uom_code").HasColumnOrder(6).HasColumnType("text");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasColumnOrder(7).HasColumnType("text");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnOrder(8).HasColumnType("text");
        builder.Property(x => x.ConditionBased).HasColumnName("condition_based").HasColumnOrder(9).HasColumnType("text");

        builder.HasOne<ImportBatch>().WithMany()
            .HasForeignKey(x => x.ImportBatchId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_catalog_row_batch").OnDelete(DeleteBehavior.Cascade);
    }
}
