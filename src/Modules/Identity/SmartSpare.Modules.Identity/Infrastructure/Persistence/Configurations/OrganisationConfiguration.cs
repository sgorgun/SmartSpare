using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>iam.position</c>: job positions with their role template (<c>iam.position_role</c>), master data of STAGE_7_PLAN
/// section 1.1 (ADR 0026). Audit trigger, grants and the seed of the standard positions: <c>db/sql/identity/0007_organisation.sql</c>.
/// </summary>
internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public const string PositionIdProperty = "PositionId";

    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("position", "iam", t =>
        {
            t.HasComment("Job positions of the dealership; the role template of a position gives its employees their application roles (ADR 0026).");
            t.HasCheckConstraint("ck_position_code", "((code)::text ~ '^[A-Z0-9_]{2,30}$'::text)");
            t.HasCheckConstraint("ck_position_archived", "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
        });

        builder.HasKey(x => x.Id).HasName("pk_position");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("smallint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(30)").HasComment("Business key, e.g. BRAND_MANAGER.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2).HasColumnType("character varying(120)").HasComment("Display name.");
        builder.Property(x => x.Description).HasColumnName("description").HasColumnOrder(3).HasColumnType("character varying(500)");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(4).HasColumnType("boolean").HasDefaultValueSql("false").ValueGeneratedNever().HasComment("Archived instead of deleted; cannot be chosen for employees.");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnOrder(5).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ArchivedBy).HasColumnName("archived_by").HasColumnOrder(6).HasColumnType("uuid").HasComment("User who archived the position (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(7).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("uq_position_code");
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_position_name_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");

        builder.HasMany(x => x.Roles).WithOne()
            .HasForeignKey(PositionIdProperty)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_position_role_position").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.RoleIds);
        builder.Ignore(x => x.DomainEvents);
    }
}

/// <summary><c>iam.position_role</c>: the role template of a position.</summary>
internal sealed class PositionRoleConfiguration : IEntityTypeConfiguration<PositionRole>
{
    public void Configure(EntityTypeBuilder<PositionRole> builder)
    {
        builder.ToTable("position_role", "iam", t => t.HasComment("Role template: the application roles every employee of the position has."));

        builder.Property<short>(PositionConfiguration.PositionIdProperty).HasColumnName("position_id").HasColumnOrder(0).HasColumnType("smallint");
        builder.Property(x => x.Id).HasColumnName("role_id").HasColumnOrder(1).HasColumnType("smallint").ValueGeneratedNever().HasComment("Reference to iam.role.");
        builder.Ignore(x => x.RoleId);
        builder.HasKey(PositionConfiguration.PositionIdProperty, nameof(PositionRole.Id)).HasName("pk_position_role");

        builder.HasOne<Role>().WithMany()
            .HasForeignKey(x => x.Id)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_position_role_role").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.Id).HasDatabaseName("ix_position_role_role");
    }
}

/// <summary>
/// <c>iam.employee</c>: the people of the dealership (STAGE_7_PLAN section 1.1). Personal data (full_name, email, phone)
/// is masked in <c>iam.v_employee_masked</c> and excluded from the audit values.
/// </summary>
internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employee", "iam", t =>
        {
            t.HasComment("Employees of the dealership: the single source of who is who (ADR 0026). Contains personal data (full_name, email, phone).");
            t.HasCheckConstraint("ck_employee_code", "((code)::text ~ '^E[0-9]{4,6}$'::text)");
            t.HasCheckConstraint("ck_employee_email", "((email IS NULL) OR (email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'::citext))");
            t.HasCheckConstraint("ck_employee_phone", "((phone IS NULL) OR ((phone)::text ~ '^\\+?[0-9 ()-]{7,20}$'::text))");
            t.HasCheckConstraint("ck_employee_dates", "((left_on IS NULL) OR (hired_on IS NULL) OR (left_on >= hired_on))");
            t.HasCheckConstraint("ck_employee_archived", "((is_archived AND (archived_at IS NOT NULL) AND (left_on IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
            t.HasCheckConstraint("ck_employee_anonymised_archived", "((anonymised_at IS NULL) OR is_archived)");
        });

        builder.HasKey(x => x.Id).HasName("pk_employee");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedNever();
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(10)").HasComment("Employee number, e.g. E0001.");
        builder.Property(x => x.FullName).HasColumnName("full_name").HasColumnOrder(2).HasColumnType("character varying(150)").HasComment("PERSONAL DATA (GDPR). Masked by util.mask_name for read-only roles.");
        builder.Property(x => x.Email).HasConversion(v => v == null ? null : v.Value, v => v == null ? null : EmailAddress.FromDatabase(v))
            .HasColumnName("email").HasColumnOrder(3).HasColumnType("citext").HasComment("PERSONAL DATA (GDPR). Corporate e-mail.");
        builder.Property(x => x.Phone).HasColumnName("phone").HasColumnOrder(4).HasColumnType("character varying(30)").HasComment("PERSONAL DATA (GDPR).");
        builder.Property(x => x.PositionId).HasColumnName("position_id").HasColumnOrder(5).HasColumnType("smallint").HasComment("The position; its role template gives the account's roles.");
        builder.Property(x => x.HiredOn).HasColumnName("hired_on").HasColumnOrder(6).HasColumnType("date");
        builder.Property(x => x.LeftOn).HasColumnName("left_on").HasColumnOrder(7).HasColumnType("date").HasComment("Set when the employee is archived (left the company).");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(8).HasColumnType("boolean").HasDefaultValueSql("false").ValueGeneratedNever().HasComment("TRUE = left the company; the account cannot sign in.");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnOrder(9).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ArchivedBy).HasColumnName("archived_by").HasColumnOrder(10).HasColumnType("uuid");
        builder.Property(x => x.AnonymisedAt).HasColumnName("anonymised_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasComment("Set by iam.anonymise_employee() (GDPR Art. 17).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(12).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(13).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property<uint>("xmin").IsRowVersion();
        builder.Ignore(x => x.IsActive);
        builder.Ignore(x => x.DomainEvents);

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("uq_employee_code");
        builder.HasIndex(x => x.Email).IsUnique().HasDatabaseName("uq_employee_email");
        builder.HasIndex(x => x.PositionId).HasDatabaseName("ix_employee_position");
        builder.HasIndex(x => x.FullName).HasDatabaseName("ix_employee_full_name_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");

        builder.HasOne<Position>().WithMany()
            .HasForeignKey(x => x.PositionId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_employee_position").OnDelete(DeleteBehavior.Restrict);
    }
}
