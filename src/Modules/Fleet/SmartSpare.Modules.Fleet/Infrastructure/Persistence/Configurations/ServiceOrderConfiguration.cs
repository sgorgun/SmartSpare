using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.service_order</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class ServiceOrderConfiguration : IEntityTypeConfiguration<ServiceOrder>
{
    public void Configure(EntityTypeBuilder<ServiceOrder> builder)
    {
        builder.ToTable("service_order", "fleet", t =>
        {
            t.HasComment("Service order (SO-YYYY-NNNNNN). Planned (plan + due hours) or unplanned repair (plan NULL). Completed orders form the maintenance history.");
            t.HasCheckConstraint("ck_service_order_cancel", "((status = 'cancelled'::fleet.service_order_status) = (cancel_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_service_order_completed", "((status = 'completed'::fleet.service_order_status) = ((completed_at IS NOT NULL) AND (completed_engine_hours IS NOT NULL)))");
            t.HasCheckConstraint("ck_service_order_number", "((number)::text ~ '^SO-\\d{4}-\\d{6}$'::text)");
            t.HasCheckConstraint("ck_service_order_planned_due", "((maintenance_plan_id IS NULL) OR (due_engine_hours IS NOT NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_service_order");
        builder.HasAlternateKey(x => x.Number).HasName("uq_service_order_number");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Number).HasColumnName("number").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Business document number (assigned by trigger).");
        builder.Property(x => x.MachineId).HasColumnName("machine_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to fleet.machine.");
        builder.Property(x => x.MaintenancePlanId).HasColumnName("maintenance_plan_id").HasColumnOrder(3).HasColumnType("integer").HasComment("Reference to fleet.maintenance_plan.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(4).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(5).HasDefaultValueSql("'draft'::fleet.service_order_status").HasComment("Workflow status (state machine guarded by trigger).").ValueGeneratedNever();
        builder.Property(x => x.DueEngineHours).HasColumnName("due_engine_hours").HasColumnOrder(6).HasColumnType("numeric(8,1)").HasComment("Engine-hour threshold this planned service covers (e.g. 500, 1000, 1500 ...).");
        builder.Property(x => x.TriggerEngineHours).HasColumnName("trigger_engine_hours").HasColumnOrder(7).HasColumnType("numeric(8,1)").HasComment("Engine hours of the reading that triggered the draft.");
        builder.Property(x => x.CompletedEngineHours).HasColumnName("completed_engine_hours").HasColumnOrder(8).HasColumnType("numeric(8,1)");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnOrder(9).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasComment("Time of the completed event.");
        builder.Property(x => x.CancelReason).HasColumnName("cancel_reason").HasColumnOrder(12).HasColumnType("character varying(300)");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(13).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_service_order_created_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_service_order_warehouse -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<Machine>().WithMany()
            .HasForeignKey(x => x.MachineId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_service_order_machine").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<MaintenancePlan>().WithMany()
            .HasForeignKey(x => x.MaintenancePlanId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_service_order_plan").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.CreatedBy).HasDatabaseName("ix_service_order_created_by");
        builder.HasIndex(x => new { x.WarehouseId, x.Status }).HasDatabaseName("ix_service_order_open").HasFilter("(status = ANY (ARRAY['draft'::fleet.service_order_status, 'parts_reserved'::fleet.service_order_status, 'in_progress'::fleet.service_order_status]))");
        builder.HasIndex(x => x.MaintenancePlanId).HasDatabaseName("ix_service_order_plan");
        builder.HasIndex(x => new { x.MachineId, x.MaintenancePlanId, x.DueEngineHours }).HasDatabaseName("uq_service_order_machine_plan_due").IsUnique().HasFilter("((status <> 'cancelled'::fleet.service_order_status) AND (maintenance_plan_id IS NOT NULL))");
    }
}
