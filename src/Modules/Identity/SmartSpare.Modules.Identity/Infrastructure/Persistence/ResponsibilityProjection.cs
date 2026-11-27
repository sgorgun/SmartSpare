using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Identity.Domain.Organisation;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Read-only projection of the rights-bearing responsible employees of brands and warehouses (ADR 0026 decision 3),
/// written only by the <c>ResponsibilitiesChanged</c> consumer. Not a domain aggregate: the facts are edited in the
/// module that owns the record.
/// </summary>
internal sealed class EmployeeResponsibility
{
    public string RecordKind { get; set; } = null!;

    public short RecordId { get; set; }

    public Guid EmployeeId { get; set; }

    public string AssignmentRole { get; set; } = null!;
}

/// <summary>The time of the newest applied event per record, so a late or replayed event never overwrites newer data.</summary>
internal sealed class ResponsibilitySource
{
    public string RecordKind { get; set; } = null!;

    public short RecordId { get; set; }

    public DateTimeOffset SourceOccurredAt { get; set; }
}

internal sealed class EmployeeResponsibilityConfiguration : IEntityTypeConfiguration<EmployeeResponsibility>
{
    public void Configure(EntityTypeBuilder<EmployeeResponsibility> builder)
    {
        builder.ToTable("employee_responsibility", "iam", t =>
        {
            t.HasComment("Projection of the rights-bearing responsible employees of brands and warehouses (ADR 0026); written only by the ResponsibilitiesChanged consumer.");
            t.HasCheckConstraint("ck_employee_responsibility_kind", "record_kind IN ('brand', 'warehouse')");
        });
        builder.HasKey(x => new { x.RecordKind, x.RecordId, x.EmployeeId, x.AssignmentRole }).HasName("pk_employee_responsibility");
        builder.Property(x => x.RecordKind).HasColumnName("record_kind").HasColumnType("character varying(20)");
        builder.Property(x => x.RecordId).HasColumnName("record_id").HasColumnType("smallint").HasComment("Brand or warehouse id of the Inventory module; no FK across modules.");
        builder.Property(x => x.EmployeeId).HasColumnName("employee_id").HasColumnType("uuid");
        builder.Property(x => x.AssignmentRole).HasColumnName("assignment_role").HasColumnType("character varying(30)");
        builder.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).HasPrincipalKey(e => e.Id)
            .HasConstraintName("fk_employee_responsibility_employee").OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.EmployeeId).HasDatabaseName("ix_employee_responsibility_employee");
    }
}

internal sealed class ResponsibilitySourceConfiguration : IEntityTypeConfiguration<ResponsibilitySource>
{
    public void Configure(EntityTypeBuilder<ResponsibilitySource> builder)
    {
        builder.ToTable("responsibility_source", "iam", t => t.HasComment("Newest applied ResponsibilitiesChanged per record (order-safe projection)."));
        builder.HasKey(x => new { x.RecordKind, x.RecordId }).HasName("pk_responsibility_source");
        builder.Property(x => x.RecordKind).HasColumnName("record_kind").HasColumnType("character varying(20)");
        builder.Property(x => x.RecordId).HasColumnName("record_id").HasColumnType("smallint");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnType("timestamp with time zone");
    }
}
