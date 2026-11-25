using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>
/// Publishes outbox messages to the publishing module's topic exchange (ADR 0017 §2–§3). The channel has publisher
/// confirms with tracking, so a publish completes only when the broker confirmed it; a nack, a return (no queue bound,
/// <c>mandatory</c>) or a confirm timeout throws, and the outbox counts a failed attempt and backs off (#31).
/// </summary>
internal sealed class RabbitMqIntegrationEventPublisher(RabbitMqConnection connection, IOptions<RabbitMqOptions> options)
    : IIntegrationEventPublisher, IAsyncDisposable
{
    /// <summary>The W3C trace context header, as in HTTP (ADR 0018 §3).</summary>
    public const string TraceParentHeader = "traceparent";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _declaredExchanges = new(StringComparer.Ordinal);
    private IChannel? _channel;

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var eventType = IntegrationEventSerializer.ResolveType(envelope.Type);
        var exchange = IntegrationEventTopology.Exchange(eventType);
        var routingKey = IntegrationEventTopology.RoutingKey(eventType);

        using var activity = IntegrationEventDelivery.StartPublishActivity($"{exchange} publish", envelope);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);

        var properties = new BasicProperties
        {
            MessageId = envelope.Id.ToString(),
            Type = envelope.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        if ((activity ?? Activity.Current) is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            properties.Headers[TraceParentHeader] = traceParent;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
            if (!_declaredExchanges.Contains(exchange))
            {
                await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _declaredExchanges.Add(exchange);
            }

            using var confirm = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirm.CancelAfter(options.Value.ConfirmTimeout);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(envelope.Payload), confirm.Token)
                .ConfigureAwait(false);
        }
        catch (PublishException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new InvalidOperationException(ex.IsReturn
                ? $"No queue is bound for {routingKey} on {exchange}; the event stays in the outbox until its consumer has declared its queue."
                : $"The broker did not confirm {routingKey} on {exchange}.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new TimeoutException($"The broker did not confirm {routingKey} within {options.Value.ConfirmTimeout.TotalSeconds} s.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _declaredExchanges.Clear();
        }

        var amqp = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        _channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken)
            .ConfigureAwait(false);
        return _channel;
    }
}
