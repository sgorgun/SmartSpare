using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>Read-only copy of an Inventory brand (STAGE_7_PLAN 3.2, PR 7h1), written only through <c>iam.apply_brands</c>.</summary>
internal sealed class BrandSnapshot
{
    public short BrandId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsArchived { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// <c>iam.brand_snapshot</c>: Identity's copy of Inventory's brands with the same ids, for the codes in the user list and
/// the active brands in the user form. Grants and the write function: <c>db/sql/identity/0015_brand_snapshot.sql</c>.
/// </summary>
internal sealed class BrandSnapshotConfiguration : IEntityTypeConfiguration<BrandSnapshot>
{
    public void Configure(EntityTypeBuilder<BrandSnapshot> builder)
    {
        builder.ToTable("brand_snapshot", "iam", t =>
            t.HasComment("Read-only copy of the Inventory brands, fed by BrandCatalogChanged. Written only by iam.apply_brands."));

        builder.HasKey(x => x.BrandId).HasName("pk_brand_snapshot");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(0).HasColumnType("smallint").ValueGeneratedNever().HasComment("Inventory brand id (catalog.brand.id); no cross-module FK.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Brand code, e.g. CLAAS.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(80)").HasComment("Brand name.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(3).HasColumnType("boolean").HasComment("TRUE = archived in the catalogue; not offered for new assignments.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(4).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");
    }
}
