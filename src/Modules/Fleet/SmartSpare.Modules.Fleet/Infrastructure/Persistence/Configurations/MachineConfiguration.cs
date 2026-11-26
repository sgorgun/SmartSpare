using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.machine</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> builder)
    {
        builder.ToTable("machine", "fleet", t =>
        {
            t.HasComment("Registered machine (installed base). current_engine_hours is maintained by the engine-hour reading trigger.");
            t.HasCheckConstraint("ck_machine_commissioned", "((commissioned_on IS NULL) OR (EXTRACT(year FROM commissioned_on) >= (manufacture_year)::numeric))");
            t.HasCheckConstraint("ck_machine_engine_hours", "((current_engine_hours >= (0)::numeric) AND (current_engine_hours <= (100000)::numeric))");
            t.HasCheckConstraint("ck_machine_vin", "((vin)::text ~ '^[A-Z0-9]{8,20}$'::text)");
            t.HasCheckConstraint("ck_machine_year", "((manufacture_year >= 1950) AND (manufacture_year <= 2100))");
        });

        builder.HasKey(x => x.Id).HasName("pk_machine");
        builder.HasAlternateKey(x => x.Vin).HasName("uq_machine_vin");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Vin).HasColumnName("vin").HasColumnOrder(1).HasColumnType("character varying(20)").HasComment("Vehicle / product identification number (unique).");
        builder.Property(x => x.ModelId).HasColumnName("model_id").HasColumnOrder(2).HasColumnType("integer").HasComment("Reference to fleet.machine_model.");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id").HasColumnOrder(3).HasColumnType("bigint").HasComment("Reference to fleet.customer.");
        builder.Property(x => x.OperatingConditionId).HasColumnName("operating_condition_id").HasColumnOrder(4).HasColumnType("smallint").HasComment("Operating condition that shortens OEM intervals (F5).");
        builder.Property(x => x.HomeWarehouseId).HasColumnName("home_warehouse_id").HasColumnOrder(5).HasColumnType("smallint").HasComment("Nearest depot where service kits are reserved.");
        builder.Property(x => x.ManufactureYear).HasColumnName("manufacture_year").HasColumnOrder(6).HasColumnType("smallint");
        builder.Property(x => x.CurrentEngineHours).HasColumnName("current_engine_hours").HasColumnOrder(7).HasColumnType("numeric(8,1)").HasDefaultValueSql("0").HasComment("Latest odometer value; maintained by the reading trigger.");
        builder.Property(x => x.EngineHoursReadAt).HasColumnName("engine_hours_read_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasComment("Time of the engine hours read event.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(9).HasDefaultValueSql("'active'::fleet.machine_status").HasComment("Workflow status (state machine guarded by trigger).").ValueGeneratedNever();
        builder.Property(x => x.CommissionedOn).HasColumnName("commissioned_on").HasColumnOrder(10).HasColumnType("date");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(12).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).");
        // fk_machine_home_wh -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<RefOperatingCondition>().WithMany()
            .HasForeignKey(x => x.OperatingConditionId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_machine_condition").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Customer>().WithMany()
            .HasForeignKey(x => x.CustomerId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_machine_customer").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<MachineModel>().WithMany()
            .HasForeignKey(x => x.ModelId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_machine_model").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.OperatingConditionId).HasDatabaseName("ix_machine_condition");
        builder.HasIndex(x => x.CustomerId).HasDatabaseName("ix_machine_customer");
        builder.HasIndex(x => x.HomeWarehouseId).HasDatabaseName("ix_machine_home_wh_active").HasFilter("(status = 'active'::fleet.machine_status)");
        builder.HasIndex(x => x.ModelId).HasDatabaseName("ix_machine_model");
        builder.HasIndex(x => x.Vin).HasDatabaseName("ix_machine_vin_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}
