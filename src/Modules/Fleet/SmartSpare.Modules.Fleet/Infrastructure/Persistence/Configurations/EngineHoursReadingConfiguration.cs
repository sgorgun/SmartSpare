using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Domain.Machines;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.engine_hours_reading</c> mapped onto the append-only <see cref="EngineHoursReading"/> (V016 unchanged; ADR 0013).</summary>
internal sealed class EngineHoursReadingConfiguration : IEntityTypeConfiguration<EngineHoursReading>
{
    public void Configure(EntityTypeBuilder<EngineHoursReading> builder)
    {
        builder.ToTable("engine_hours_reading", "fleet", t =>
        {
            t.HasComment("Odometer history (US05). Monotonic and physically plausible readings are enforced by trigger.");
            t.HasCheckConstraint("ck_engine_hours_reading_hours", "((reading_hours >= (0)::numeric) AND (reading_hours <= (100000)::numeric))");
            t.HasCheckConstraint("ck_engine_hours_reading_not_future", "(read_at <= (created_at + '00:05:00'::interval))");
        });

        builder.HasKey(x => x.Id).HasName("pk_engine_hours_reading");
        builder.HasAlternateKey(x => new { x.MachineId, x.ReadAt }).HasName("uq_engine_hours_reading_machine_time");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.MachineId).HasColumnName("machine_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to fleet.machine.");
        builder.Property(x => x.ReadingHours).HasColumnName("reading_hours").HasColumnOrder(2).HasColumnType("numeric(8,1)").HasComment("Odometer value; must not decrease and must be physically plausible.");
        builder.Property(x => x.ReadAt).HasColumnName("read_at").HasColumnOrder(3).HasColumnType("timestamp with time zone").HasComment("Time the reading was taken in the field (may be earlier than created_at for offline entry).");
        builder.Property(x => x.Source).HasColumnName("source").HasColumnOrder(4).HasDefaultValueSql("'manual'::fleet.reading_source").ValueGeneratedNever();
        builder.Property(x => x.RecordedBy).HasColumnName("recorded_by").HasColumnOrder(5).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(6).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        // fk_engine_hours_reading_user -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<Machine>().WithMany()
            .HasForeignKey(x => x.MachineId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_engine_hours_reading_machine").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.RecordedBy).HasDatabaseName("ix_engine_hours_reading_user");
    }
}
