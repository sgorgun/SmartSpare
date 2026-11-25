using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>Back-off schedule, metric and health check of the outbox (#31).</summary>
public sealed class OutboxBackoffTests
{
    private static readonly OutboxOptions Defaults = new();

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(7, 320)]
    [InlineData(8, 600)]     // capped at 10 minutes
    [InlineData(40, 600)]    // no overflow for very large counts
    public void The_delay_doubles_from_five_seconds_up_to_ten_minutes(int failures, double seconds) =>
        OutboxBackoff.Delay(failures, Defaults, sample: 0.5).TotalSeconds.ShouldBe(seconds, tolerance: 1e-9);

    [Fact]
    public void Jitter_stays_within_twenty_percent()
    {
        OutboxBackoff.Delay(3, Defaults, sample: 0).TotalSeconds.ShouldBe(16, tolerance: 1e-9);
        OutboxBackoff.Delay(3, Defaults, sample: 0.999999).TotalSeconds.ShouldBe(24, tolerance: 1e-3);
    }

    [Fact]
    public void All_ten_default_attempts_ride_out_about_half_an_hour()
    {
        var total = Enumerable.Range(1, Defaults.MaxAttempts - 1).Sum(f => OutboxBackoff.Delay(f, Defaults, 0.5).TotalMinutes);
        total.ShouldBeInRange(25, 35);
    }

    [Fact]
    public void The_failure_count_starts_at_one() =>
        Should.Throw<ArgumentOutOfRangeException>(() => OutboxBackoff.Delay(0, Defaults, 0.5));

    [Fact]
    public async Task The_health_check_is_degraded_while_messages_are_parked()
    {
        using var monitor = NewMonitor(out _);
        var check = new OutboxHealthCheck(monitor);

        (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);

        monitor.Update([new OutboxStatus("iam", 0, null), new OutboxStatus("inventory", 3, null)]);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("inventory 3");
        result.Data.ShouldContainKeyAndValue("inventory", 3L);
    }

    [Fact]
    public void The_gauges_report_parked_messages_and_the_oldest_pending_age_per_schema()
    {
        using var monitor = NewMonitor(out var time);
        monitor.Update([new OutboxStatus("iam", 2, time.GetUtcNow().AddSeconds(-90)), new OutboxStatus("fleet", 0, null)]);
        var measured = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OutboxMonitor.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => measured[$"{i.Name}/{tags[0].Value}"] = v);
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => measured[$"{i.Name}/{tags[0].Value}"] = v);
        listener.Start();

        listener.RecordObservableInstruments();

        measured["smartspare.outbox.exhausted/iam"].ShouldBe(2);
        measured["smartspare.outbox.exhausted/fleet"].ShouldBe(0);
        measured["smartspare.outbox.oldest_pending_age/iam"].ShouldBe(90, tolerance: 1e-6);
        measured["smartspare.outbox.oldest_pending_age/fleet"].ShouldBe(0);
    }

    private static OutboxMonitor NewMonitor(out FakeTimeProvider time)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        return new OutboxMonitor(services.GetRequiredService<IMeterFactory>(), time);
    }
}
