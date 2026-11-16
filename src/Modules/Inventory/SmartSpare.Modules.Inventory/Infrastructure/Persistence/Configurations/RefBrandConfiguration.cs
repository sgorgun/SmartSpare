using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.brand</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefBrandConfiguration : IEntityTypeConfiguration<RefBrand>
{
    public void Configure(EntityTypeBuilder<RefBrand> builder)
    {
        builder.ToTable("brand", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
    }
}
