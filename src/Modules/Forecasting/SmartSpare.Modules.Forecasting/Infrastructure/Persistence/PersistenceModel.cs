namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>PostgreSQL enum <c>forecast.forecast_method</c>.</summary>
internal enum ForecastMethod
{
    Zero,
    Ma3,
    Ses,
    Croston,
    Sba,
    Fasttree,
}

/// <summary>PostgreSQL enum <c>forecast.model_status</c>.</summary>
internal enum ModelStatus
{
    Training,
    Candidate,
    Active,
    Retired,
    Rejected,
}

/// <summary>PostgreSQL enum <c>forecast.recommendation_kind</c>.</summary>
internal enum RecommendationKind
{
    Transfer,
    Purchase,
}

/// <summary>PostgreSQL enum <c>forecast.recommendation_status</c>.</summary>
internal enum RecommendationStatus
{
    Open,
    Accepted,
    Overridden,
    Rejected,
    Expired,
}

/// <summary>PostgreSQL enum <c>forecast.run_status</c>.</summary>
internal enum RunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>Row of <c>forecast.benchmark_result</c>.</summary>
internal sealed class BenchmarkResult
{
    public int ModelVersionId { get; set; }

    public ForecastMethod Method { get; set; }

    public string DemandClassCode { get; set; } = null!;

    public int SeriesCount { get; set; }

    public decimal Rmsse { get; set; }

    public decimal Mase { get; set; }
}

/// <summary>Row of <c>forecast.demand_forecast</c>.</summary>
internal sealed class DemandForecast
{
    public long ForecastRunId { get; set; }

    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public DateOnly WeekStart { get; set; }

    public decimal ForecastQty { get; set; }
}

/// <summary>Row of <c>forecast.forecast_run</c>.</summary>
internal sealed class ForecastRun
{
    public long Id { get; set; }

    public int ModelVersionId { get; set; }

    public RunStatus Status { get; set; }

    public short HorizonWeeks { get; set; }

    public DateOnly FirstWeek { get; set; }

    public string TriggeredBy { get; set; } = null!;

    public Guid? RequestedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>Row of <c>forecast.ml_model_version</c>.</summary>
internal sealed class MlModelVersion
{
    public int Id { get; set; }

    public ForecastMethod Algorithm { get; set; }

    public string VersionLabel { get; set; } = null!;

    public string? DemandClassCode { get; set; }

    public ModelStatus Status { get; set; }

    public DateOnly TrainingFrom { get; set; }

    public DateOnly TrainingTo { get; set; }

    public DateOnly HoldoutFrom { get; set; }

    public DateOnly HoldoutTo { get; set; }

    public decimal? Rmsse { get; set; }

    public decimal? Mase { get; set; }

    public decimal? Mae { get; set; }

    public decimal? Rmse { get; set; }

    public string Hyperparameters { get; set; } = null!;

    public string? ArtifactUri { get; set; }

    public string? ArtifactSha256 { get; set; }

    public DateTimeOffset? TrainedAt { get; set; }

    public DateTimeOffset? PromotedAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>forecast.recommendation</c>.</summary>
internal sealed class Recommendation
{
    public long Id { get; set; }

    public long ForecastRunId { get; set; }

    public RecommendationKind Kind { get; set; }

    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public short? DonorWarehouseId { get; set; }

    public int? SupplierId { get; set; }

    public decimal BaseDemandQty { get; set; }

    public decimal MaintenanceDemandQty { get; set; }

    public decimal LeadTimeBufferQty { get; set; }

    public decimal SafetyStockQty { get; set; }

    public decimal AvailableQty { get; set; }

    public decimal InTransitQty { get; set; }

    public decimal NetPositionQty { get; set; }

    public decimal RecommendedQty { get; set; }

    public decimal EstimatedCostEur { get; set; }

    public decimal? AlternativeCostEur { get; set; }

    public RecommendationStatus Status { get; set; }

    public decimal? FinalQty { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? DecisionReason { get; set; }

    public long? TransferOrderId { get; set; }

    public long? PurchaseOrderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Platform reference table <c>ref.demand_class</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefDemandClass
{
    public string Code { get; set; } = null!;
}
