using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>ref.assignment_role</c>, read-only (Platform reference data, ADR 0026).</summary>
internal sealed class RefAssignmentRoleConfiguration : IEntityTypeConfiguration<RefAssignmentRole>
{
    public void Configure(EntityTypeBuilder<RefAssignmentRole> builder)
    {
        builder.ToTable("assignment_role", "ref", t => t.ExcludeFromMigrations());
        builder.HasKey(x => new { x.RecordKind, x.Code });
        builder.Property(x => x.RecordKind).HasColumnName("record_kind").HasColumnType("character varying(20)");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnType("character varying(30)");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("character varying(80)");
        builder.Property(x => x.GrantsScope).HasColumnName("grants_scope").HasColumnType("boolean");
    }
}

/// <summary>Shared mapping of the responsible-employee tables (STAGE_7_PLAN 3.7): one pattern, one table per record kind.</summary>
internal static class ResponsibleMapping
{
    public const string RecordKindProperty = "RecordKind";

    public static void Configure<T>(EntityTypeBuilder<T> builder, string schema, string table, string kind)
        where T : SmartSpare.SharedKernel.Entity<long>, IResponsibility
    {
        builder.ToTable(table, schema, t =>
        {
            t.HasComment($"Responsible employees of a {kind} with their assignment role (ADR 0026); the employee is an id of the Identity module.");
            t.HasCheckConstraint($"ck_{table}_kind", $"record_kind = '{kind}'");
        });
        builder.HasKey(x => x.Id).HasName($"pk_{table}");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").UseIdentityByDefaultColumn();
        builder.Property(x => x.EmployeeId).HasColumnName("employee_id").HasColumnType("uuid").HasComment("Employee (iam.employee); no FK across modules.");
        builder.Property<string>(RecordKindProperty).HasColumnName("record_kind").HasColumnType("character varying(20)").HasDefaultValue(kind)
            .HasComment("Always the record kind of this table; part of the FK to ref.assignment_role.");
        builder.Property(x => x.Role).HasColumnName("assignment_role").HasColumnType("character varying(30)").HasComment("ref.assignment_role.code for this record kind.");
        builder.Property<DateTimeOffset>(nameof(BrandResponsible.CreatedAt)).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property<Guid?>(nameof(BrandResponsible.CreatedBy)).HasColumnName("created_by").HasColumnType("uuid").HasComment("User who assigned it.");
        builder.HasOne<RefAssignmentRole>().WithMany()
            .HasForeignKey(RecordKindProperty, nameof(IResponsibility.Role))
            .HasPrincipalKey(r => new { r.RecordKind, r.Code })
            .HasConstraintName($"fk_{table}_role").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.EmployeeId).HasDatabaseName($"ix_{table}_employee");
    }
}

internal sealed class BrandResponsibleConfiguration : IEntityTypeConfiguration<BrandResponsible>
{
    public void Configure(EntityTypeBuilder<BrandResponsible> builder)
    {
        ResponsibleMapping.Configure(builder, "catalog", "brand_responsible", RecordKinds.Brand);
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnType("smallint");
        builder.HasOne<Domain.Catalog.Brand>().WithMany().HasForeignKey(x => x.BrandId).HasPrincipalKey(b => b.Id)
            .HasConstraintName("fk_brand_responsible_brand").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.BrandId, x.EmployeeId, x.Role }).IsUnique().HasDatabaseName("uq_brand_responsible");
    }
}

internal sealed class WarehouseResponsibleConfiguration : IEntityTypeConfiguration<WarehouseResponsible>
{
    public void Configure(EntityTypeBuilder<WarehouseResponsible> builder)
    {
        ResponsibleMapping.Configure(builder, "inventory", "warehouse_responsible", RecordKinds.Warehouse);
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnType("smallint");
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).HasPrincipalKey(w => w.Id)
            .HasConstraintName("fk_warehouse_responsible_warehouse").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.WarehouseId, x.EmployeeId, x.Role }).IsUnique().HasDatabaseName("uq_warehouse_responsible");
    }
}
