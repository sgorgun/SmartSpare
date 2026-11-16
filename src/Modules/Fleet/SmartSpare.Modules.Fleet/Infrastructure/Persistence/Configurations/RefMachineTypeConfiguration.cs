using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.machine_type</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefMachineTypeConfiguration : IEntityTypeConfiguration<RefMachineType>
{
    public void Configure(EntityTypeBuilder<RefMachineType> builder)
    {
        builder.ToTable("machine_type", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("smallint").ValueGeneratedNever();
    }
}
