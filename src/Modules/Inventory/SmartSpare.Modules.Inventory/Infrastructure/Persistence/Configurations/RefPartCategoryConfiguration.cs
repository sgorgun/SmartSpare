using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.part_category</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefPartCategoryConfiguration : IEntityTypeConfiguration<RefPartCategory>
{
    public void Configure(EntityTypeBuilder<RefPartCategory> builder)
    {
        builder.ToTable("part_category", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("integer").ValueGeneratedNever();
    }
}
