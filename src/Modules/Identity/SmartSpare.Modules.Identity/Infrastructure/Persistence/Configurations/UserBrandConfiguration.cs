using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary><c>iam.user_brand</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class UserBrandConfiguration : IEntityTypeConfiguration<UserBrand>
{
    public void Configure(EntityTypeBuilder<UserBrand> builder)
    {
        builder.ToTable("user_brand", "iam", t =>
        {
            t.HasComment("M:N brand manager <-> brands they are responsible for (e.g. persona Tomas: Claas and Lemken).");
        });

        builder.HasKey(x => new { x.UserId, x.BrandId }).HasName("pk_user_brand");

        builder.Property(x => x.UserId).HasColumnName("user_id").HasColumnOrder(0).HasColumnType("uuid").HasComment("Reference to iam.app_user.");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to ref.brand.");

        builder.HasOne<RefBrand>().WithMany()
            .HasForeignKey(x => x.BrandId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_brand_brand").OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany()
            .HasForeignKey(x => x.UserId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_brand_user").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.BrandId).HasDatabaseName("ix_user_brand_brand");
    }
}
