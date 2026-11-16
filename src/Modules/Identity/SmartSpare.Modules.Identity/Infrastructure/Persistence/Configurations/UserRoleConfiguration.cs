using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary><c>iam.user_role</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_role", "iam", t =>
        {
            t.HasComment("M:N user <-> role assignment.");
        });

        builder.HasKey(x => new { x.UserId, x.RoleId }).HasName("pk_user_role");

        builder.Property(x => x.UserId).HasColumnName("user_id").HasColumnOrder(0).HasColumnType("uuid").HasComment("Reference to iam.app_user.");
        builder.Property(x => x.RoleId).HasColumnName("role_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to iam.role.");
        builder.Property(x => x.GrantedAt).HasColumnName("granted_at").HasColumnOrder(2).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the granted event.");
        builder.Property(x => x.GrantedBy).HasColumnName("granted_by").HasColumnOrder(3).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");

        builder.HasOne<AppUser>().WithMany()
            .HasForeignKey(x => x.GrantedBy)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_granted_by").OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Role>().WithMany()
            .HasForeignKey(x => x.RoleId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_role").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany()
            .HasForeignKey(x => x.UserId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_user").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.GrantedBy).HasDatabaseName("ix_user_role_granted_by");
        builder.HasIndex(x => x.RoleId).HasDatabaseName("ix_user_role_role");
    }
}
