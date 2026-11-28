using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>catalog.manufacturer</c>: who makes a part (STAGE_7_PLAN 3.1, PR 7h3, ADR 0025). Triggers and grants:
/// <c>db/sql/inventory/0017_manufacturers.sql</c>.
/// </summary>
internal sealed class ManufacturerConfiguration : IEntityTypeConfiguration<Manufacturer>
{
    public const string Table = "manufacturer";

    public void Configure(EntityTypeBuilder<Manufacturer> builder)
    {
        builder.ToTable(Table, "catalog", t =>
        {
            t.HasComment("Makers of parts, e.g. MANN+HUMMEL; a part may name one (ADR 0025).");
            t.HasCheckConstraint("ck_manufacturer_code", "((code)::text ~ '^[A-Z0-9_]{2,20}$'::text)");
            t.HasCheckConstraint("ck_manufacturer_country", "((country_code IS NULL) OR (country_code ~ '^[A-Z]{2}$'::text))");
            t.HasArchiveCheck(Table);
        });

        builder.MapMasterRecord<Manufacturer, short>(Table, 20, "MANN");
        builder.Property(x => x.Id).HasColumnType("smallint");
        builder.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnOrder(3).HasColumnType("character(2)")
            .HasComment("ISO 3166-1 alpha-2 country, if known.");
        builder.Property(x => x.Website).HasColumnName("website").HasColumnOrder(4).HasColumnType($"character varying({Manufacturer.MaxWebsiteLength})")
            .HasComment("Website, if known.");
    }
}
