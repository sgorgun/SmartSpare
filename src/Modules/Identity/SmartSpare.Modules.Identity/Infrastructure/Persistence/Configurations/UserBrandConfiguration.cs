using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="UserBrandAssignment"/> (child of <see cref="AppUser"/>) onto <c>iam.user_brand</c> as created by
/// V016 (names, types, constraints and comments unchanged). The owning user is a shadow foreign key.
/// </summary>
internal sealed class UserBrandConfiguration : IEntityTypeConfiguration<UserBrandAssignment>
{
    public const string UserIdProperty = "UserId";

    public void Configure(EntityTypeBuilder<UserBrandAssignment> builder)
    {
        builder.ToTable("user_brand", "iam", t =>
        {
            t.HasComment("M:N brand manager <-> brands they are responsible for (e.g. persona Tomas: Claas and Lemken).");
        });

        builder.Property<Guid>(UserIdProperty).HasColumnName("user_id").HasColumnOrder(0).HasColumnType("uuid").HasComment("Reference to iam.app_user.");
        builder.Property(x => x.Id).HasColumnName("brand_id").HasColumnOrder(1).HasColumnType("smallint").ValueGeneratedNever().HasComment("Reference to ref.brand.");
        builder.Ignore(x => x.BrandId);

        builder.HasKey(UserIdProperty, nameof(UserBrandAssignment.Id)).HasName("pk_user_brand");

        // fk_user_brand_user is configured from the aggregate (AppUserConfiguration, navigation Brands).
        builder.HasOne<RefBrand>().WithMany()
            .HasForeignKey(x => x.Id)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_brand_brand").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.Id).HasDatabaseName("ix_user_brand_brand");
    }
}
