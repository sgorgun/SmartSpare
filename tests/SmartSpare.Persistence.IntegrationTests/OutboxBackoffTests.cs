using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>
/// Back-off and parking of failed deliveries on real PostgreSQL (#31): a failed message waits for its next attempt,
/// a message out of attempts is parked, and the status, the health check and mart.v_data_quality report it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class OutboxBackoffTests(MigratedDatabaseFixture db) : IAsyncLifetime
{
    private static readonly Guid MessageId = Guid.Parse("3c1f7a52-31a1-4b31-9d31-000000000031");
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModulePersistence<ProbeDbContext>(typeof(OutboxBackoffTests).Assembly, db.AdminConnectionString, "iam", outboxSchema: "iam");
        services.AddSingleton<IIntegrationEventPublisher, BrokerDown>();     // every delivery fails
        services.AddOutboxDispatcher(o => o.MaxAttempts = 2);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await db.ScalarAsync<int>($$"""
            WITH m AS (INSERT INTO iam.outbox_message (id, occurred_at, type, payload)
                       VALUES ('{{MessageId}}', now(), 'SmartSpare.Probe, SmartSpare.Probe', '{}') RETURNING 1)
            SELECT 0
            """);
    }

    [Fact]
    public async Task A_failed_message_backs_off_and_is_parked_after_its_last_attempt()
    {
        var dispatcher = _provider.GetServices<IHostedService>().OfType<OutboxDispatcher>().Single();
        var ct = TestContext.Current.CancellationToken;

        // First failure: one attempt, the error, and the next attempt 5 s (+/- 20 %) away.
        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(1);
        (await db.ScalarAsync<int>(Column("attempts"))).ShouldBe(1);
        (await db.ScalarAsync<string>(Column("error"))).ShouldBe("broker down");
        var wait = await db.ScalarAsync<double>(Column("extract(epoch FROM next_attempt_at - now())::float8"));
        wait.ShouldBeInRange(3.0, 6.5);
        (await db.ScalarAsync<bool>(Column("exhausted_at IS NULL"))).ShouldBeTrue();

        // Not due yet: the next pass leaves it alone.
        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(0);

        // Due: the second failure uses up MaxAttempts = 2 and parks the message.
        await db.ScalarAsync<int>($"WITH u AS (UPDATE iam.outbox_message SET next_attempt_at = now() - interval '1 second' WHERE id = '{MessageId}' RETURNING 1) SELECT 0");
        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(1);
        (await db.ScalarAsync<int>(Column("attempts"))).ShouldBe(2);
        (await db.ScalarAsync<bool>(Column("exhausted_at IS NOT NULL AND next_attempt_at IS NULL AND processed_at IS NULL"))).ShouldBeTrue();
        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(0);

        // Reported: status, health check and the reconciliation view.
        await dispatcher.RefreshStatusAsync(ct);
        var monitor = _provider.GetRequiredService<OutboxMonitor>();
        monitor.Statuses.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.Schema.ShouldBe("iam"),
            s => s.Exhausted.ShouldBe(1));
        var health = await new OutboxHealthCheck(monitor).CheckHealthAsync(new HealthCheckContext(), ct);
        health.Status.ShouldBe(HealthStatus.Degraded);
        health.Description!.ShouldContain("iam 1");
        (await db.ScalarAsync<long>(
            "SELECT failed_rows FROM mart.v_data_quality WHERE check_name = 'integration events parked in an outbox'")).ShouldBe(1);

        // The operator's replay (docs/operations/OUTBOX.md) makes it deliverable again.
        await db.ScalarAsync<int>($"""
            WITH u AS (UPDATE iam.outbox_message SET attempts = 0, next_attempt_at = NULL, exhausted_at = NULL, error = NULL
                       WHERE id = '{MessageId}' RETURNING 1)
            SELECT 0
            """);
        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(1);
    }

    private static string Column(string expression) => $"SELECT {expression} FROM iam.outbox_message WHERE id = '{MessageId}'";

    public async ValueTask DisposeAsync()
    {
        await db.ScalarAsync<int>($"WITH d AS (DELETE FROM iam.outbox_message WHERE id = '{MessageId}' RETURNING 1) SELECT 0");
        await _provider.DisposeAsync();
    }

    private sealed class BrokerDown : IIntegrationEventPublisher
    {
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("broker down");
    }
}
