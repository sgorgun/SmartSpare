using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>forecast.benchmark_result</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class BenchmarkResultConfiguration : IEntityTypeConfiguration<BenchmarkResult>
{
    public void Configure(EntityTypeBuilder<BenchmarkResult> builder)
    {
        builder.ToTable("benchmark_result", "forecast", t =>
        {
            t.HasComment("Holdout accuracy of a candidate model and of the 5 benchmarks per demand class (KPI-02: ML must have >= 10% lower RMSSE).");
            t.HasCheckConstraint("ck_benchmark_result_metrics", "((rmsse >= (0)::numeric) AND (mase >= (0)::numeric))");
            t.HasCheckConstraint("ck_benchmark_result_series", "(series_count > 0)");
        });

        builder.HasKey(x => new { x.ModelVersionId, x.Method, x.DemandClassCode }).HasName("pk_benchmark_result");

        builder.Property(x => x.ModelVersionId).HasColumnName("model_version_id").HasColumnOrder(0).HasColumnType("integer").HasComment("Reference to forecast.ml_model_version.");
        builder.Property(x => x.Method).HasColumnName("method").HasColumnOrder(1);
        builder.Property(x => x.DemandClassCode).HasColumnName("demand_class_code").HasColumnOrder(2).HasColumnType("character varying(15)").HasComment("Reference to ref.demand_class.");
        builder.Property(x => x.SeriesCount).HasColumnName("series_count").HasColumnOrder(3).HasColumnType("integer");
        builder.Property(x => x.Rmsse).HasColumnName("rmsse").HasColumnOrder(4).HasColumnType("numeric(8,4)");
        builder.Property(x => x.Mase).HasColumnName("mase").HasColumnOrder(5).HasColumnType("numeric(8,4)");

        builder.HasOne<RefDemandClass>().WithMany()
            .HasForeignKey(x => x.DemandClassCode)
            .HasPrincipalKey(x => x.Code)
            .HasConstraintName("fk_benchmark_result_class").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<MlModelVersion>().WithMany()
            .HasForeignKey(x => x.ModelVersionId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_benchmark_result_model").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.DemandClassCode).HasDatabaseName("ix_benchmark_result_class");
    }
}
