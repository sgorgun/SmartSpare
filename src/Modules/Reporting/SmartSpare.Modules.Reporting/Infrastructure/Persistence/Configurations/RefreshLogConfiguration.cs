using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Configurations;

/// <summary><c>mart.refresh_log</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class RefreshLogConfiguration : IEntityTypeConfiguration<RefreshLog>
{
    public void Configure(EntityTypeBuilder<RefreshLog> builder)
    {
        builder.ToTable("refresh_log", "mart", t =>
        {
            t.HasComment("Monitoring of data mart refreshes (duration, row count, failures).");
            // Source text of V014: the catalogue form of IN (...) on varchar does not round-trip.
            t.HasCheckConstraint("ck_refresh_log_status", "status IN ('ok', 'failed')");
            t.HasCheckConstraint("ck_refresh_log_time", "(finished_at >= started_at)");
        });

        builder.HasKey(x => x.Id).HasName("pk_refresh_log");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ViewName).HasColumnName("view_name").HasColumnOrder(1).HasColumnType("character varying(80)");
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnOrder(2).HasColumnType("timestamp with time zone").HasComment("Time of the started event.");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnOrder(3).HasColumnType("timestamp with time zone").HasComment("Time of the finished event.");
        builder.Property(x => x.RowCount).HasColumnName("row_count").HasColumnOrder(4).HasColumnType("bigint");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(5).HasColumnType("character varying(10)").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.Message).HasColumnName("message").HasColumnOrder(6).HasColumnType("text");

        builder.HasIndex(x => new { x.ViewName, x.StartedAt }).HasDatabaseName("ix_refresh_log_view_time").IsDescending(false, true);
    }
}
