using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence.Configurations;

/// <summary><c>procurement.supplier</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("supplier", "procurement", t =>
        {
            t.HasComment("OEM suppliers / distributors. Business contact data only (no personal data).");
            t.HasCheckConstraint("ck_supplier_country", "(country_code ~ '^[A-Z]{2}$'::text)");
            t.HasCheckConstraint("ck_supplier_currency", "(currency_code ~ '^[A-Z]{3}$'::text)");
            t.HasCheckConstraint("ck_supplier_email", "((order_email IS NULL) OR (order_email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext))");
            t.HasCheckConstraint("ck_supplier_lead_time", "((default_lead_time_days >= 1) AND (default_lead_time_days <= 365))");
        });

        builder.HasKey(x => x.Id).HasName("pk_supplier");
        builder.HasAlternateKey(x => x.Code).HasName("uq_supplier_code");
        builder.HasAlternateKey(x => x.Name).HasName("uq_supplier_name");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("integer").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Business code (natural key).");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(200)").HasComment("Display name.");
        builder.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnOrder(3).HasColumnType("character(2)");
        builder.Property(x => x.OrderEmail).HasColumnName("order_email").HasColumnOrder(4).HasColumnType("citext").HasComment("Shared order mailbox of the company (e.g. orders@...), not a personal address.");
        builder.Property(x => x.DefaultLeadTimeDays).HasColumnName("default_lead_time_days").HasColumnOrder(5).HasColumnType("smallint");
        builder.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasColumnOrder(6).HasColumnType("character(3)").HasDefaultValueSql("'EUR'::bpchar");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(7).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(9).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
    }
}
