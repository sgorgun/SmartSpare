using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.maintenance_plan</c> mapped onto the <see cref="MaintenancePlan"/> aggregate (V016 plus the calendar interval of STAGE_6_PLAN F3; ADR 0013).</summary>
internal sealed class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        builder.ToTable("maintenance_plan", "fleet", t =>
        {
            t.HasComment("OEM service interval of a model (e.g. 500 h engine oil service). The effective interval = interval_hours x operating_condition.interval_factor.");
            t.HasCheckConstraint("ck_maintenance_plan_interval", "((interval_hours >= 10) AND (interval_hours <= 20000))");
            t.HasCheckConstraint("ck_maintenance_plan_interval_months", "((interval_months IS NULL) OR ((interval_months >= 1) AND (interval_months <= 60)))");
        });

        builder.HasKey(x => x.Id).HasName("pk_maintenance_plan");
        builder.HasAlternateKey(x => new { x.ModelId, x.IntervalHours }).HasName("uq_maintenance_plan_model_interval");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("integer").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ModelId).HasColumnName("model_id").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to fleet.machine_model.");
        builder.Property(x => x.IntervalHours).HasColumnName("interval_hours").HasColumnOrder(2).HasColumnType("integer").HasComment("OEM interval in engine hours (e.g. 500).");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(3).HasColumnType("character varying(120)").HasComment("Display name.");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(4).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.").HasSentinel(true);
        builder.Property(x => x.IntervalMonths).HasColumnName("interval_months").HasColumnOrder(5).HasColumnType("smallint").HasComment("OEM calendar interval in months, if any; the service is due by engine hours or by calendar, whichever comes first.");

        builder.HasOne<MachineModel>().WithMany()
            .HasForeignKey(x => x.ModelId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_maintenance_plan_model").OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Parts).WithOne()
            .HasForeignKey(x => x.MaintenancePlanId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_maintenance_plan_part_plan").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Parts).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}
