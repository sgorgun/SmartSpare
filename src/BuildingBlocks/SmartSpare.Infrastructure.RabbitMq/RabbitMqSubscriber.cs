using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>The consumers of this service, discovered from its registrations once the container is built.</summary>
internal sealed record RabbitMqSubscriptions(IReadOnlyList<IntegrationEventSubscription> All);

/// <summary>
/// Consumes this service's queues (ADR 0017 §2–§3). At start-up it declares, idempotently, every exchange, queue, binding
/// and dead-letter queue its consumers need, so an expired or new queue comes back on the next start. Each message runs
/// through <see cref="IntegrationEventDelivery"/> (handler and inbox in the module's transaction) and is acknowledged
/// after the commit; a failure is rejected with requeue, and the quorum queue dead-letters the message once it reaches
/// <see cref="RabbitMqOptions.DeliveryLimit"/>.
/// </summary>
internal sealed partial class RabbitMqSubscriber(
    RabbitMqConnection connection,
    RabbitMqSubscriptions subscriptions,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqSubscriber> logger) : BackgroundService
{
    private IChannel? _channel;

    /// <summary>Completes once the consumers are subscribed (tests wait for it).</summary>
    public Task Ready => _ready.Task;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (subscriptions.All.Count == 0)
        {
            _ready.TrySetResult();
            return;
        }

        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SubscribeAsync(stoppingToken).ConfigureAwait(false);
                _ready.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The broker may be down at start-up; keep trying, the outbox keeps the events meanwhile.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscribeFailed(ex, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var amqp = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        _channel = await amqp.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _channel.BasicQosAsync(0, options.Value.Prefetch, global: false, cancellationToken).ConfigureAwait(false);

        foreach (var module in subscriptions.All.Select(s => s.ConsumingModule).Distinct(StringComparer.Ordinal))
        {
            await DeclareDeadLettersAsync(_channel, module, cancellationToken).ConfigureAwait(false);
        }

        foreach (var subscription in subscriptions.All)
        {
            await DeclareQueueAsync(_channel, subscription, cancellationToken).ConfigureAwait(false);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            var channel = _channel;
            consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, subscription, delivery);
            await _channel.BasicConsumeAsync(subscription.Queue, autoAck: false, consumerTag: subscription.Queue, consumer, cancellationToken)
                .ConfigureAwait(false);
            LogSubscribed(subscription.Queue, subscription.Exchange, subscription.RoutingKey);
        }
    }

    private static async Task DeclareDeadLettersAsync(IChannel channel, string module, CancellationToken ct)
    {
        var exchange = IntegrationEventTopology.DeadLetterExchange(module);
        var queue = IntegrationEventTopology.DeadLetterQueue(module);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct).ConfigureAwait(false);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct).ConfigureAwait(false);
        await channel.QueueBindAsync(queue, exchange, "#", cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task DeclareQueueAsync(IChannel channel, IntegrationEventSubscription subscription, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(subscription.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct)
            .ConfigureAwait(false);
        // Quorum queue with a delivery limit; dead-lettering at least once into the module's dead-letter queue, which
        // needs overflow = reject-publish (RabbitMQ quorum queue docs). Queue arguments here; a broker policy may override.
        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = options.Value.DeliveryLimit,
            ["x-dead-letter-exchange"] = IntegrationEventTopology.DeadLetterExchange(subscription.ConsumingModule),
            ["x-dead-letter-routing-key"] = subscription.Queue,
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
        };
        await channel.QueueDeclareAsync(subscription.Queue, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: ct)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(subscription.Queue, subscription.Exchange, subscription.RoutingKey, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task HandleAsync(IChannel channel, IntegrationEventSubscription subscription, BasicDeliverEventArgs delivery)
    {
        var parent = ParentContext(delivery.BasicProperties);
        using var activity = IntegrationEventDelivery.ActivitySource.StartActivity($"{subscription.Queue} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", subscription.Queue);
        activity?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);

        try
        {
            var messageId = Guid.Parse(delivery.BasicProperties.MessageId!);
            var integrationEvent = IntegrationEventSerializer.Deserialize(delivery.BasicProperties.Type!, Encoding.UTF8.GetString(delivery.Body.Span));

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var consumer = scope.ServiceProvider.GetServices(subscription.ServiceType).OfType<object>()
                    .Single(c => c.GetType() == subscription.ConsumerType);
                var registry = scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>();
                await IntegrationEventDelivery.DeliverAsync(scope.ServiceProvider, registry, consumer, integrationEvent, messageId, delivery.CancellationToken)
                    .ConfigureAwait(false);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, delivery.CancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any consumer failure is a redelivery; the quorum queue's delivery limit dead-letters a poison message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogConsumeFailed(ex, subscription.Queue, delivery.BasicProperties.MessageId);
            // basic.reject, not basic.nack: on quorum queues only reject increments the delivery count that the delivery
            // limit is checked against (RabbitMQ quorum queue docs, "Poison Message Handling").
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The publisher's trace context from the <c>traceparent</c> header, so the trace continues across the broker.</summary>
    private static ActivityContext ParentContext(IReadOnlyBasicProperties properties)
    {
        var value = properties.Headers?.TryGetValue(RabbitMqIntegrationEventPublisher.TraceParentHeader, out var header) == true
            ? header switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;
        return ActivityContext.TryParse(value, null, out var context) ? context : default;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue} (exchange {Exchange}, routing key {RoutingKey}).")]
    private partial void LogSubscribed(string queue, string exchange, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not subscribe to RabbitMQ; retrying in {Seconds} s. Events wait in the outbox meanwhile.")]
    private partial void LogSubscribeFailed(Exception exception, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer of {Queue} failed on message {MessageId}; it is redelivered until the delivery limit, then dead-lettered.")]
    private partial void LogConsumeFailed(Exception exception, string queue, string? messageId);
}
