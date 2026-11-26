using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the <see cref="AppUser"/> aggregate directly onto <c>iam.app_user</c> as created by V016 (names, types,
/// constraints and comments unchanged; ADR 0013). <c>xmin</c> is the optimistic concurrency token.
/// </summary>
internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_user", "iam", t =>
        {
            t.HasComment("Dealership employees using SmartSpare. Contains personal data (full_name, email, phone): masked for app_read / app_audit.");
            t.HasCheckConstraint("ck_app_user_active_has_password", "((password_hash IS NOT NULL) OR (NOT is_active))");
            t.HasCheckConstraint("ck_app_user_anonymised_inactive", "((anonymised_at IS NULL) OR (NOT is_active))");
            t.HasCheckConstraint("ck_app_user_email", "(email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext)");
            t.HasCheckConstraint("ck_app_user_failed_logins", "((failed_login_count >= 0) AND (failed_login_count <= 100))");
            t.HasCheckConstraint("ck_app_user_password_hash_format", "((password_hash IS NULL) OR (password_hash ~ '^AQAAAA[A-Za-z0-9+/]{76,}={0,2}$'::text))");
            t.HasCheckConstraint("ck_app_user_phone", "((phone IS NULL) OR ((phone)::text ~ '^\\+?[0-9 ()-]{7,20}$'::text))");
            // The regex matches case-insensitively on citext, so the second part keeps stored names lower-case (#69):
            // the text comparison is case-sensitive, unlike a citext one, which would always be true.
            t.HasCheckConstraint("ck_app_user_user_name", "((user_name ~ '^[a-z0-9._-]{3,50}$'::citext) AND ((user_name)::text = lower((user_name)::text)))");
        });

        builder.HasKey(x => x.Id).HasName("pk_app_user");
        builder.HasAlternateKey(x => x.Email).HasName("uq_app_user_email");
        builder.HasAlternateKey(x => x.UserName).HasName("uq_app_user_user_name");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()").HasComment("Surrogate primary key.");
        builder.Property(x => x.UserName).HasColumnName("user_name").HasColumnOrder(1).HasColumnType("citext")
            .HasConversion(v => v.Value, v => UserName.FromDatabase(v));
        builder.Property(x => x.Email).HasConversion(v => v.Value, v => EmailAddress.FromDatabase(v)).HasColumnName("email").HasColumnOrder(2).HasColumnType("citext").HasComment("PERSONAL DATA (GDPR). Corporate e-mail; masked by util.mask_email for read-only roles.");
        builder.Property(x => x.FullName).HasColumnName("full_name").HasColumnOrder(3).HasColumnType("character varying(150)").HasComment("PERSONAL DATA (GDPR). Masked by util.mask_name for read-only roles.");
        builder.Property(x => x.Phone).HasColumnName("phone").HasColumnOrder(4).HasColumnType("character varying(30)").HasComment("PERSONAL DATA (GDPR). Masked by util.mask_phone for read-only roles.");
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").HasColumnOrder(5).HasColumnType("text").HasComment("ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 220,000 iterations; older hashes are rehashed at sign-in). Plain text is impossible by CHECK.");
        builder.Property(x => x.SecurityStamp).HasColumnName("security_stamp").HasColumnOrder(6).HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()").HasComment("Changes on password/role change; invalidates issued sign-in cookies (security stamp validation).");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(7).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count").HasColumnOrder(8).HasColumnType("smallint").HasDefaultValueSql("0");
        builder.Property(x => x.LockoutEnd).HasColumnName("lockout_end").HasColumnOrder(9).HasColumnType("timestamp with time zone");
        builder.Property(x => x.HomeWarehouseId).HasColumnName("home_warehouse_id").HasColumnOrder(10).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.LastLoginAt).HasColumnName("last_login_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasComment("Time of the last login event.");
        builder.Property(x => x.AnonymisedAt).HasColumnName("anonymised_at").HasColumnOrder(12).HasColumnType("timestamp with time zone").HasComment("Set by iam.anonymise_user() (GDPR Art. 17); the row is kept so audit references stay valid.");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(13).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(14).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_app_user_home_warehouse -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasIndex(x => x.HomeWarehouseId).HasDatabaseName("ix_app_user_home_warehouse");

        // PostgreSQL's xmin system column as the concurrency token: no column is created, the
        // migration that introduced it is schema-free (STAGE_3_PLAN section 10, 3a).
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasMany(x => x.Roles).WithOne()
            .HasForeignKey(UserRoleConfiguration.UserIdProperty)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_role_user").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.Brands).WithOne()
            .HasForeignKey(UserBrandConfiguration.UserIdProperty)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_user_brand_user").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Brands).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(x => x.DomainEvents);
    }
}
