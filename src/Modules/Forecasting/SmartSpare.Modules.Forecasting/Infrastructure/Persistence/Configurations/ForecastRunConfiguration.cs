using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>forecast.forecast_run</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class ForecastRunConfiguration : IEntityTypeConfiguration<ForecastRun>
{
    public void Configure(EntityTypeBuilder<ForecastRun> builder)
    {
        builder.ToTable("forecast_run", "forecast", t =>
        {
            t.HasComment("One execution of the forecasting pipeline (Quartz.NET weekly or user-triggered, horizon 4-12 weeks).");
            t.HasCheckConstraint("ck_forecast_run_error", "((status = 'failed'::forecast.run_status) = (error_message IS NOT NULL))");
            t.HasCheckConstraint("ck_forecast_run_finished", "((status = 'running'::forecast.run_status) = (finished_at IS NULL))");
            t.HasCheckConstraint("ck_forecast_run_first_week_monday", "(EXTRACT(isodow FROM first_week) = (1)::numeric)");
            t.HasCheckConstraint("ck_forecast_run_horizon", "((horizon_weeks >= 4) AND (horizon_weeks <= 12))");
            t.HasCheckConstraint("ck_forecast_run_timeline", "((finished_at IS NULL) OR (finished_at >= started_at))");
            // Source text of V008: the catalogue form of IN (...) on varchar does not round-trip.
            t.HasCheckConstraint("ck_forecast_run_trigger", "triggered_by IN ('scheduler', 'user')");
            t.HasCheckConstraint("ck_forecast_run_user", "(((triggered_by)::text = 'scheduler'::text) OR (requested_by IS NOT NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_forecast_run");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ModelVersionId).HasColumnName("model_version_id").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to forecast.ml_model_version.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(2).HasDefaultValueSql("'running'::forecast.run_status").HasComment("Workflow status (state machine guarded by trigger).").ValueGeneratedNever();
        builder.Property(x => x.HorizonWeeks).HasColumnName("horizon_weeks").HasColumnOrder(3).HasColumnType("smallint");
        builder.Property(x => x.FirstWeek).HasColumnName("first_week").HasColumnOrder(4).HasColumnType("date");
        builder.Property(x => x.TriggeredBy).HasColumnName("triggered_by").HasColumnOrder(5).HasColumnType("character varying(10)");
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasColumnOrder(6).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnOrder(7).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the started event.");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasComment("Time of the finished event.");
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasColumnOrder(9).HasColumnType("text");
        // fk_forecast_run_user -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<MlModelVersion>().WithMany()
            .HasForeignKey(x => x.ModelVersionId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_forecast_run_model").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.StartedAt).HasDatabaseName("ix_forecast_run_latest").IsDescending(true).HasFilter("(status = 'succeeded'::forecast.run_status)");
        builder.HasIndex(x => x.ModelVersionId).HasDatabaseName("ix_forecast_run_model");
        builder.HasIndex(x => x.RequestedBy).HasDatabaseName("ix_forecast_run_user");
    }
}
