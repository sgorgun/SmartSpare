using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.operating_condition</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefOperatingConditionConfiguration : IEntityTypeConfiguration<RefOperatingCondition>
{
    public void Configure(EntityTypeBuilder<RefOperatingCondition> builder)
    {
        builder.ToTable("operating_condition", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
    }
}
