using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.demand_class</c> belongs to the Platform (PlatformDbContext); mapped only as an FK principal.</summary>
internal sealed class RefDemandClassConfiguration : IEntityTypeConfiguration<RefDemandClass>
{
    public void Configure(EntityTypeBuilder<RefDemandClass> builder)
    {
        builder.ToTable("demand_class", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasColumnName("code").HasColumnType("character varying(15)").ValueGeneratedNever();
    }
}
