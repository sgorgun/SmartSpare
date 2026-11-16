using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.maintenance_plan</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        builder.ToTable("maintenance_plan", "fleet", t =>
        {
            t.HasComment("OEM service interval of a model (e.g. 500 h engine oil service). The effective interval = interval_hours x operating_condition.interval_factor.");
            t.HasCheckConstraint("ck_maintenance_plan_interval", "((interval_hours >= 10) AND (interval_hours <= 20000))");
        });

        builder.HasKey(x => x.Id).HasName("pk_maintenance_plan");
        builder.HasAlternateKey(x => new { x.ModelId, x.IntervalHours }).HasName("uq_maintenance_plan_model_interval");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("integer").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ModelId).HasColumnName("model_id").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to fleet.machine_model.");
        builder.Property(x => x.IntervalHours).HasColumnName("interval_hours").HasColumnOrder(2).HasColumnType("integer").HasComment("OEM interval in engine hours (e.g. 500).");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(3).HasColumnType("character varying(120)").HasComment("Display name.");
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnOrder(4).HasColumnType("boolean").HasDefaultValueSql("true").HasComment("FALSE = deactivated, kept for history.");

        builder.HasOne<MachineModel>().WithMany()
            .HasForeignKey(x => x.ModelId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_maintenance_plan_model").OnDelete(DeleteBehavior.Cascade);
    }
}
