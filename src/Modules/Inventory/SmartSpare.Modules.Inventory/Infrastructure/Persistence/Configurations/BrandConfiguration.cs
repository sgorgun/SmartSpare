using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>catalog.brand</c>: the brands, owned by Inventory since PR 7h1 (STAGE_7_PLAN 3.2). The table was moved from
/// <c>ref.brand</c> with its ids and foreign keys and given the master-data columns by
/// <c>db/sql/inventory/0016_brand_move.sql</c>, which also holds its triggers and grants.
/// </summary>
internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public const string Table = "brand";

    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable(Table, "catalog", t =>
        {
            t.HasComment("Machinery / OEM brands (Claas, John Deere, Fendt, Massey Ferguson, Lemken, Väderstad ...).");
            t.HasCheckConstraint("ck_brand_country", "country_code ~ '^[A-Z]{2}$'");
            t.HasArchiveCheck(Table);
        });

        builder.MapMasterRecord<Brand, short>(Table, 10, "CLAAS");
        builder.Property(x => x.Id).HasColumnType("smallint");
        builder.Property(x => x.Name).HasColumnType($"character varying({Brand.MaxNameLength})");
        builder.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnOrder(3).HasColumnType("character(2)")
            .HasComment("ISO 3166-1 alpha-2 country of the manufacturer.");
        builder.HasIndex(x => x.Name, "uq_brand_name").IsUnique().HasDatabaseName("uq_brand_name");
    }
}
