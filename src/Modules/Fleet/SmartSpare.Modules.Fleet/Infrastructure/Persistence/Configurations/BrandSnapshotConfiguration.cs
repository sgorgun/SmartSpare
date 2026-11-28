using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>fleet.brand_snapshot</c> (STAGE_7_PLAN 3.2, PR 7h1): Fleet's read-only copy of Inventory's brands, with the same
/// ids, so machine models keep their <c>brand_id</c> without a cross-module FK. Grants, the write function and the view
/// that reads it:
/// <c>db/sql/fleet/0011_brand_snapshot.sql</c>.
/// </summary>
internal sealed class BrandSnapshotConfiguration : IEntityTypeConfiguration<BrandSnapshot>
{
    public void Configure(EntityTypeBuilder<BrandSnapshot> builder)
    {
        builder.ToTable("brand_snapshot", "fleet", t =>
            t.HasComment("Read-only copy of the Inventory brands, fed by BrandCatalogChanged. Written only by fleet.apply_brands."));

        builder.HasKey(x => x.BrandId).HasName("pk_brand_snapshot");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(0).HasColumnType("smallint").ValueGeneratedNever().HasComment("Inventory brand id (catalog.brand.id); no cross-module FK.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Brand code, e.g. CLAAS.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(80)").HasComment("Brand name.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(3).HasColumnType("boolean").HasComment("TRUE = archived in the catalogue.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(4).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");
    }
}
