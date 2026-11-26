using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.customer</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customer", "fleet", t =>
        {
            t.HasComment("Machine owner (farm). Pseudonymised: only a code and an HMAC of the ERP id are stored, no names or contacts.");
            t.HasCheckConstraint("ck_customer_code", "((customer_code)::text ~ '^CUST-\\d{6}$'::text)");
            t.HasCheckConstraint("ck_customer_erp_ref_hash", "((erp_ref_hash IS NULL) OR (length(erp_ref_hash) = 32))");
            t.HasCheckConstraint("ck_customer_farm_size", "((farm_size_ha IS NULL) OR (farm_size_ha > (0)::numeric))");
        });

        builder.HasKey(x => x.Id).HasName("pk_customer");
        builder.HasAlternateKey(x => x.CustomerCode).HasName("uq_customer_code");
        // uq_customer_erp_ref_hash (UNIQUE (erp_ref_hash)) is created in SQL: the column is nullable and EF alternate keys are required.

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.CustomerCode).HasColumnName("customer_code").HasColumnOrder(1).HasColumnType("character varying(12)");
        builder.Property(x => x.ErpRefHash).HasColumnName("erp_ref_hash").HasColumnOrder(2).HasColumnType("bytea").HasComment("HMAC-SHA256 of the ERP customer id (util.pseudonymise); allows matching re-imports without storing the id.");
        builder.Property(x => x.RegionWarehouseId).HasColumnName("region_warehouse_id").HasColumnOrder(3).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.FarmSizeHa).HasColumnName("farm_size_ha").HasColumnOrder(4).HasColumnType("numeric(8,1)");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        // fk_customer_region_warehouse -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasIndex(x => x.RegionWarehouseId).HasDatabaseName("ix_customer_region_warehouse");
    }
}
