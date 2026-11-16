namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>Row of <c>mart.refresh_log</c>.</summary>
internal sealed class RefreshLog
{
    public long Id { get; set; }

    public string ViewName { get; set; } = null!;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public long? RowCount { get; set; }

    public string Status { get; set; } = null!;

    public string? Message { get; set; }
}
