using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary><c>iam.permission</c>: the permission catalogue of the code (ADR 0028). Seed, grants: <c>db/sql/identity/0011_permissions.sql</c>.</summary>
internal sealed class PermissionDefinitionConfiguration : IEntityTypeConfiguration<PermissionDefinition>
{
    public void Configure(EntityTypeBuilder<PermissionDefinition> builder)
    {
        builder.ToTable("permission", "iam", t =>
        {
            t.HasComment("The fixed permission catalogue defined in code (<Module>.<Resource>.<Action>, ADR 0028); roles bundle these.");
            t.HasCheckConstraint("ck_permission_code", @"((code)::text ~ '^[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+$'::text)");
        });
        builder.HasKey(x => x.Code).HasName("pk_permission");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(0).HasColumnType("character varying(100)").HasComment("Permission name, e.g. Inventory.Transfer.Approve.");
        builder.Property(x => x.Module).HasColumnName("module").HasColumnOrder(1).HasColumnType("character varying(30)").HasComment("Module, for grouping in the UI.");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(2).HasColumnType("character varying(200)").HasComment("What the permission allows.");
    }
}

/// <summary><c>iam.role_permission</c>: the permissions of each role (ADR 0028). Audit trigger and the default bundles: SQL.</summary>
internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permission", "iam", t => t.HasComment("Role bundles: a role grants exactly these permissions (additive, no deny rules; ADR 0028)."));
        builder.HasKey(x => new { x.RoleId, x.Permission }).HasName("pk_role_permission");
        builder.Property(x => x.RoleId).HasColumnName("role_id").HasColumnOrder(0).HasColumnType("smallint").HasComment("Reference to iam.role.");
        builder.Property(x => x.Permission).HasColumnName("permission").HasColumnOrder(1).HasColumnType("character varying(100)").HasComment("Reference to iam.permission.");
        builder.HasIndex(x => x.Permission).HasDatabaseName("ix_role_permission_permission");
        builder.HasOne<PermissionDefinition>().WithMany().HasForeignKey(x => x.Permission).HasConstraintName("fk_role_permission_permission").OnDelete(DeleteBehavior.Restrict);
    }
}
