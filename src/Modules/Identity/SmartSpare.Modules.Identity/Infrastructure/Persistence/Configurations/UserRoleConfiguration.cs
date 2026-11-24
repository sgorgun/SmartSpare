using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="UserRoleAssignment"/> (child of <see cref="AppUser"/>) onto <c>iam.user_role</c> as created by
/// V016 (names, types, constraints and comments unchanged). The owning user is a shadow foreign key.
/// </summary>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public const string UserIdProperty = "UserId";

    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_role", "iam", t =>
        {
            t.HasComment("M:N user <-> role assignment.");
        });

        builder.Property<Guid>(UserIdProperty).HasColumnName("user_id").HasColumnOrder(0).HasColumnType("uuid").HasComment("Reference to iam.app_user.");
        builder.Property(x => x.Id).HasColumnName("role_id").HasColumnOrder(1).HasColumnType("smallint").ValueGeneratedNever().HasComment("Reference to iam.role.");
        builder.Ignore(x => x.RoleId);
        builder.Property(x => x.GrantedAt).HasColumnName("granted_at").HasColumnOrder(2).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the granted event.");
        builder.Property(x => x.GrantedBy).HasColumnName("granted_by").HasColumnOrder(3).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");

        builder.HasKey(UserIdProperty, nameof(UserRoleAssignment.Id)).HasName("pk_user_role");

        // fk_user_role_user is configured from the aggregate (AppUserConfiguration, navigation Roles).
        builder.HasOne<AppUser>().WithMany()
            .HasForeignKey(x => x.GrantedBy)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_granted_by").OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Role>().WithMany()
            .HasForeignKey(x => x.Id)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_role").OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.GrantedBy).HasDatabaseName("ix_user_role_granted_by");
        builder.HasIndex(x => x.Id).HasDatabaseName("ix_user_role_role");
    }
}
