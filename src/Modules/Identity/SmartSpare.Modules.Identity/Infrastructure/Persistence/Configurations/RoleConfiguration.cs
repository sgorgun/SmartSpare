using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Role"/> aggregate onto <c>iam.role</c> (V016, extended by ADR 0028) with its permissions.</summary>
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
        builder.Property(x => x.IsLocked).HasColumnName("is_locked").HasColumnOrder(4).HasColumnType("boolean").HasDefaultValueSql("false").ValueGeneratedNever()
            .HasComment("A built-in role that cannot be changed or archived (ADR 0028).");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(5).HasColumnType("boolean").HasDefaultValueSql("false").ValueGeneratedNever()
            .HasComment("Archived roles cannot be put in a role template.");
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).HasConstraintName("fk_role_permission_role").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.PermissionNames);
        builder.Ignore(x => x.DomainEvents);
    }
}
