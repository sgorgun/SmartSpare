using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.unit_of_measure</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefUnitOfMeasureConfiguration : IEntityTypeConfiguration<RefUnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<RefUnitOfMeasure> builder)
    {
        builder.ToTable("unit_of_measure", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
    }
}
