using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// Background service that drains every module's outbox (ADR 0003): claims batches with
/// FOR UPDATE SKIP LOCKED, publishes them in-process and marks them delivered. Safe with several
/// host instances. Polls while idle and keeps draining while there is work.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IOutboxStore> stores,
    IIntegrationEventPublisher publisher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly IOutboxStore[] _stores = [.. stores];

    /// <summary>One pass over all outboxes; returns the number of messages claimed.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var store in _stores)
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var claimed = await store.DispatchBatchAsync(scope.ServiceProvider, publisher.PublishAsync, options.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (claimed > 0)
                {
                    LogDispatched(claimed, store.Schema);
                }

                total += claimed;
            }
        }

        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The dispatcher must survive a database outage and retry.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }

            if (claimed == 0)
            {
                await Task.Delay(options.Value.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox {Schema}: dispatched {Count} message(s).")]
    private partial void LogDispatched(int count, string schema);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch failed; retrying after the polling interval.")]
    private partial void LogFailed(Exception exception);
}

public static class OutboxRegistration
{
    /// <summary>Registers the in-process publisher and the dispatcher. Called by the host when a database is configured.</summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services, Action<OutboxOptions>? configure = null)
    {
        services.AddOptions<OutboxOptions>().Configure(o => configure?.Invoke(o));
        services.TryAddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }
}
