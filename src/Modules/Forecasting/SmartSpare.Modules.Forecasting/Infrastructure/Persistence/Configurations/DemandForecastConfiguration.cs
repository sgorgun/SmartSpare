using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>forecast.demand_forecast</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class DemandForecastConfiguration : IEntityTypeConfiguration<DemandForecast>
{
    public void Configure(EntityTypeBuilder<DemandForecast> builder)
    {
        builder.ToTable("demand_forecast", "forecast", t =>
        {
            t.HasComment("Weekly forecast quantity per run, warehouse and part (ternary relationship run x warehouse x part, plus week).");
            t.HasCheckConstraint("ck_demand_forecast_monday", "(EXTRACT(isodow FROM week_start) = (1)::numeric)");
            t.HasCheckConstraint("ck_demand_forecast_qty", "(forecast_qty >= (0)::numeric)");
        });

        builder.HasKey(x => new { x.ForecastRunId, x.WarehouseId, x.PartId, x.WeekStart }).HasName("pk_demand_forecast");

        builder.Property(x => x.ForecastRunId).HasColumnName("forecast_run_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to forecast.forecast_run.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(2).HasColumnType("bigint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.WeekStart).HasColumnName("week_start").HasColumnOrder(3).HasColumnType("date").HasComment("Monday of the forecast ISO week.");
        builder.Property(x => x.ForecastQty).HasColumnName("forecast_qty").HasColumnOrder(4).HasColumnType("numeric(12,3)");
        // fk_demand_forecast_warehouse_part -> inventory.warehouse_part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<ForecastRun>().WithMany()
            .HasForeignKey(x => x.ForecastRunId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_demand_forecast_run").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.PartId, x.WarehouseId, x.WeekStart }).HasDatabaseName("ix_demand_forecast_part_wh_week");
    }
}
