using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.part_snapshot</c> (STAGE_6_PLAN section 5.2): no FK to <c>catalog</c>, the id is the Inventory part id.</summary>
internal sealed class PartSnapshotConfiguration : IEntityTypeConfiguration<PartSnapshot>
{
    public void Configure(EntityTypeBuilder<PartSnapshot> builder)
    {
        builder.ToTable("part_snapshot", "fleet", t =>
            t.HasComment("Read-only copy of the Inventory part catalogue for service kits, fed by PartCatalogChanged (MODULE_MAPPING 2.3). Written only by the event consumer."));

        builder.HasKey(x => x.PartId).HasName("pk_part_snapshot");

        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(0).HasColumnType("bigint").ValueGeneratedNever().HasComment("Inventory part id (catalog.part.id); no cross-module FK.");
        builder.Property(x => x.BrandCode).HasColumnName("brand_code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Brand code (catalog.brand.code, copied as text).");
        builder.Property(x => x.OemPartNumber).HasColumnName("oem_part_number").HasColumnOrder(2).HasColumnType("character varying(40)").HasComment("OEM part number.");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(3).HasColumnType("character varying(300)").HasComment("Description.");
        builder.Property(x => x.UnitOfMeasure).HasColumnName("unit_of_measure").HasColumnOrder(4).HasColumnType("character varying(10)").HasComment("Unit of measure code (ref.unit_of_measure.code).");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(5).HasColumnType("boolean").HasComment("FALSE = deactivated in the catalogue.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(7).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");

        builder.HasIndex(x => new { x.BrandCode, x.OemPartNumber }).HasDatabaseName("ix_part_snapshot_brand_oem");
    }
}
