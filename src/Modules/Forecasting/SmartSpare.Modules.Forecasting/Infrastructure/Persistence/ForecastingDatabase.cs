using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence;

/// <summary>Migrations of the Forecasting module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class ForecastingDatabase : ModuleDatabase<ForecastingDbContext>
{
    public static ForecastingDatabase Instance { get; } = new();

    private ForecastingDatabase()
    {
    }

    public override string Name => "forecasting";

    public override string HistorySchema => ForecastingDbContext.HistorySchema;

    protected override ForecastingDbContext CreateContext(DbContextOptions<ForecastingDbContext> options) => new(options);

    protected override void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        npgsql.MapEnum<ForecastMethod>("forecast_method", "forecast");
        npgsql.MapEnum<ModelStatus>("model_status", "forecast");
        npgsql.MapEnum<RecommendationKind>("recommendation_kind", "forecast");
        npgsql.MapEnum<RecommendationStatus>("recommendation_status", "forecast");
        npgsql.MapEnum<RunStatus>("run_status", "forecast");
    }
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class ForecastingDesignTimeFactory : IDesignTimeDbContextFactory<ForecastingDbContext>
{
    public ForecastingDbContext CreateDbContext(string[] args) =>
        ForecastingDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
