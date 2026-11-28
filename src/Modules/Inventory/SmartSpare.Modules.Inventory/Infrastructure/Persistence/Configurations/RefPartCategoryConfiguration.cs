using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.part_category</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefPartCategoryConfiguration : IEntityTypeConfiguration<Domain.Catalog.PartCategory>
{
    public void Configure(EntityTypeBuilder<Domain.Catalog.PartCategory> builder)
    {
        builder.ToTable("part_category", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("integer").ValueGeneratedNever();
        builder.Property(x => x.Code).HasColumnName("code").HasColumnType("character varying(30)");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("character varying(120)");
    }
}
