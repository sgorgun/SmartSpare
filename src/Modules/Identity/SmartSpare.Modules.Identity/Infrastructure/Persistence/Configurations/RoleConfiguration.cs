using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary><c>iam.role</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("role", "iam", t =>
        {
            t.HasComment("Application (RBAC) roles from the BA: BRAND_MANAGER, SERVICE_ENGINEER, WAREHOUSE_KEEPER, INVENTORY_PLANNER, SPONSOR, ADMINISTRATOR.");
            t.HasCheckConstraint("ck_role_code", "((code)::text ~ '^[A-Z_]{3,30}$'::text)");
        });

        builder.HasKey(x => x.Id).HasName("pk_role");
        builder.HasAlternateKey(x => x.Code).HasName("uq_role_code");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("smallint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(30)").HasComment("Business code (natural key).");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(80)").HasComment("Display name.");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(3).HasColumnType("text").HasComment("Description.");
    }
}
