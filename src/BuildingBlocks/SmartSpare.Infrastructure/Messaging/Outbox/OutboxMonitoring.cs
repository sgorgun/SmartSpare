using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// The latest status of every module outbox (#31), refreshed by <see cref="OutboxDispatcher"/> and published as
/// OpenTelemetry gauges on the <see cref="MeterName"/> meter: parked messages and the age of the oldest undelivered one.
/// </summary>
public sealed class OutboxMonitor : IDisposable
{
    /// <summary>Meter of SmartSpare's messaging (outbox, later the broker transport); exported by ServiceDefaults.</summary>
    public const string MeterName = "SmartSpare.Messaging";

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile IReadOnlyList<OutboxStatus> _statuses = [];

    public OutboxMonitor(IMeterFactory meterFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _meter = meterFactory.Create(MeterName);
        _meter.CreateObservableGauge(
            "smartspare.outbox.exhausted",
            () => _statuses.Select(s => new Measurement<long>(s.Exhausted, Schema(s))),
            unit: "{message}",
            description: "Integration events that ran out of delivery attempts and wait for an operator.");
        _meter.CreateObservableGauge(
            "smartspare.outbox.oldest_pending_age",
            () => _statuses.Select(s => new Measurement<double>(Age(s), Schema(s))),
            unit: "s",
            description: "Age of the oldest integration event still waiting for delivery (0 when none).");
    }

    public IReadOnlyList<OutboxStatus> Statuses => _statuses;

    /// <summary>Time of the last refresh; null before the dispatcher's first one.</summary>
    public DateTimeOffset? RefreshedAt { get; private set; }

    public void Update(IReadOnlyList<OutboxStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        _statuses = statuses;
        RefreshedAt = _time.GetUtcNow();
    }

    public void Dispose() => _meter.Dispose();

    private double Age(OutboxStatus status) =>
        status.OldestPending is { } oldest ? Math.Max(0, (_time.GetUtcNow() - oldest).TotalSeconds) : 0;

    private static KeyValuePair<string, object?> Schema(OutboxStatus status) => new("schema", status.Schema);
}

/// <summary>
/// Readiness check (#31): Degraded while any outbox holds parked messages. Degraded still answers 200, so a host isn't
/// taken out of rotation for it; the status and the metric make the parked messages visible.
/// </summary>
public sealed class OutboxHealthCheck(OutboxMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var parked = monitor.Statuses.Where(s => s.Exhausted > 0).ToList();
        return Task.FromResult(parked.Count == 0
            ? HealthCheckResult.Healthy(monitor.RefreshedAt is null ? "Outbox status not refreshed yet." : "No parked integration events.")
            : HealthCheckResult.Degraded(
                "Parked integration events: " + string.Join(", ", parked.Select(s => $"{s.Schema} {s.Exhausted}")) +
                ". See docs/operations/OUTBOX.md.",
                data: parked.ToDictionary(s => s.Schema, s => (object)s.Exhausted)));
    }
}
