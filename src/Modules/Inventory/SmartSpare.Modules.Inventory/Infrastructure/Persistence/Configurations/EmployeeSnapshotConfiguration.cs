using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>inventory.employee_snapshot</c> (ADR 0029 amendment 2, PR 7j1c): Inventory's read-only copy of Identity's employees,
/// for responsible assignments (active employees only, no self-granting) and the names in a record's history. Grants,
/// the write function and the self-grant trigger: <c>db/sql/inventory/0026_employee_snapshot.sql</c>.
/// </summary>
internal sealed class EmployeeSnapshotConfiguration : IEntityTypeConfiguration<EmployeeSnapshot>
{
    public void Configure(EntityTypeBuilder<EmployeeSnapshot> builder)
    {
        builder.ToTable("employee_snapshot", "inventory", t =>
            t.HasComment("Read-only copy of the Identity employees, fed by EmployeeDirectoryChanged. Written only by inventory.apply_employees. Holds a display name (personal data, anonymised with the employee)."));

        builder.HasKey(x => x.EmployeeId).HasName("pk_employee_snapshot");
        builder.Property(x => x.EmployeeId).HasColumnName("employee_id").HasColumnOrder(0).HasColumnType("uuid").ValueGeneratedNever().HasComment("Identity employee id (iam.employee.id); no cross-module FK.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Employee number.");
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnOrder(2).HasColumnType("character varying(200)").HasComment("Display name; 'Anonymised Employee' after a GDPR erasure.");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(3).HasColumnType("boolean").HasComment("FALSE = left or anonymised; no new assignments.");
        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnOrder(4).HasColumnType("uuid").HasComment("The employee's account (iam.app_user.id), if any: the actor id of the audit trail.");
        builder.Property(x => x.SourceOccurredAt).HasColumnName("source_occurred_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasComment("OccurredAt of the event that wrote the row; older events are ignored.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change.");

        builder.HasIndex(x => x.AccountId).HasDatabaseName("ix_employee_snapshot_account").IsUnique().HasFilter("account_id IS NOT NULL");
    }
}
