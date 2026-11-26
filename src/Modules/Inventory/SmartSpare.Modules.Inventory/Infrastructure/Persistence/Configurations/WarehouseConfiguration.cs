using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.warehouse</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("warehouse", "inventory", t =>
        {
            t.HasComment("Regional branch warehouses (Vilnius central hub, Kaunas, Klaipėda, Šiauliai, Panevėžys, Marijampolė, Utena).");
            t.HasCheckConstraint("ck_warehouse_code", "((code)::text ~ '^[A-Z]{3,10}$'::text)");
            t.HasCheckConstraint("ck_warehouse_lat", "((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))");
            t.HasCheckConstraint("ck_warehouse_lon", "((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))");
        });

        builder.HasKey(x => x.Id).HasName("pk_warehouse");
        builder.HasAlternateKey(x => x.Code).HasName("uq_warehouse_code");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("smallint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Business code (natural key).");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(120)").HasComment("Display name.");
        builder.Property(x => x.City).HasColumnName("city").HasColumnOrder(3).HasColumnType("character varying(80)");
        builder.Property(x => x.Address).HasColumnName("address").HasColumnOrder(4).HasColumnType("character varying(200)");
        builder.Property(x => x.IsCentralHub).HasColumnName("is_central_hub").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("Flag: central hub.").ValueGeneratedNever();
        builder.Property(x => x.Latitude).HasColumnName("latitude").HasColumnOrder(6).HasColumnType("numeric(8,5)");
        builder.Property(x => x.Longitude).HasColumnName("longitude").HasColumnOrder(7).HasColumnType("numeric(8,5)");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(8).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(9).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");

        builder.HasIndex(x => x.IsCentralHub).HasDatabaseName("uq_warehouse_single_central_hub").IsUnique().HasFilter("is_central_hub");
    }
}
