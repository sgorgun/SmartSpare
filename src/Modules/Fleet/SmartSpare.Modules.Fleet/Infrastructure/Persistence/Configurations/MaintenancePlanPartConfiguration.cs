using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.maintenance_plan_part</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class MaintenancePlanPartConfiguration : IEntityTypeConfiguration<MaintenancePlanPart>
{
    public void Configure(EntityTypeBuilder<MaintenancePlanPart> builder)
    {
        builder.ToTable("maintenance_plan_part", "fleet", t =>
        {
            t.HasComment("Service kit / bill of materials of a maintenance plan (M:N plan <-> part with quantity).");
            t.HasCheckConstraint("ck_maintenance_plan_part_qty", "(quantity > (0)::numeric)");
        });

        builder.HasKey(x => new { x.MaintenancePlanId, x.PartId }).HasName("pk_maintenance_plan_part");

        builder.Property(x => x.MaintenancePlanId).HasColumnName("maintenance_plan_id").HasColumnOrder(0).HasColumnType("integer").HasComment("Reference to fleet.maintenance_plan.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(2).HasColumnType("numeric(12,3)").HasComment("Quantity of the part in the service kit.");
        // fk_maintenance_plan_part_part -> catalog.part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<MaintenancePlan>().WithMany()
            .HasForeignKey(x => x.MaintenancePlanId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_maintenance_plan_part_plan").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_maintenance_plan_part_part");
    }
}
