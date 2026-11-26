using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// Hands one integration event to one consumer, the same way in every transport (ADR 0003, ADR 0017 §3): the consumer
/// runs in its own module's unit of work, and the inbox row under the consumer's stable name (#32) is written in that
/// transaction, so a redelivered event is skipped.
/// </summary>
public static class IntegrationEventDelivery
{
    /// <summary>Activity source of SmartSpare's messaging spans (publish and process), exported by ServiceDefaults.</summary>
    public const string ActivitySourceName = "SmartSpare.Messaging";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// The producer span of one delivery (STAGE_4_PLAN 6.5). The dispatcher publishes later, outside the request, so
    /// the span starts its own trace and <b>links</b> to the request's <c>traceparent</c> stored in the outbox; the
    /// consumers' spans are its children.
    /// </summary>
    public static Activity? StartPublishActivity(string name, OutboxEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ActivityLink[]? links = envelope.TraceParent is { } traceParent && ActivityContext.TryParse(traceParent, null, out var request)
            ? [new ActivityLink(request)]
            : null;
        var activity = ActivitySource.StartActivity(ActivityKind.Producer, parentContext: default, tags: null, links: links, name: name);
        activity?.SetTag("messaging.message.id", envelope.Id.ToString());
        return activity;
    }

    /// <summary>Delivers <paramref name="integrationEvent"/> to <paramref name="consumer"/>; false if it was handled before.</summary>
    /// <param name="scopedServices">The scope of this delivery: resolves the consumer module's unit of work.</param>
    /// <param name="registry">Maps the consumer's module assembly to its unit of work.</param>
    /// <param name="consumer">An <c>IIntegrationEventConsumer&lt;TEvent&gt;</c> for the event's type.</param>
    /// <param name="integrationEvent">The deserialized event.</param>
    /// <param name="messageId">The outbox id of the event; the inbox key together with the consumer name.</param>
    /// <param name="cancellationToken">Cancels the delivery; the transaction rolls back.</param>
    public static async Task<bool> DeliverAsync(
        IServiceProvider scopedServices, UnitOfWorkRegistry registry, object consumer, IIntegrationEvent integrationEvent,
        Guid messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var consumerType = consumer.GetType();
        var name = IntegrationEventConsumers.NameOf(consumerType);
        var handle = typeof(IIntegrationEventConsumer<>).MakeGenericType(integrationEvent.GetType())
            .GetMethod(nameof(IIntegrationEventConsumer<IIntegrationEvent>.Handle))!;
        var unitOfWork = registry.Resolve(consumerType.Assembly, scopedServices);

        // An event caused by a user is handled as that user (STAGE_6_PLAN 5.5): set before the transaction starts, because
        // SessionContextInterceptor writes the session context when it does. Ended after this consumer's delivery.
        using var actingAs = integrationEvent is IActedByUser acted && scopedServices.GetService(typeof(ActingUser)) is ActingUser acting
            ? acting.ActAs(acted.ActorId, "event:" + integrationEvent.GetType().Name)
            : null;

        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (!await unitOfWork.TryAcceptInboxMessageAsync(messageId, name, ct).ConfigureAwait(false))
            {
                return false;                         // handled before: idempotent receiver
            }

            try
            {
                await ((Task)handle.Invoke(consumer, [integrationEvent, ct])!).ConfigureAwait(false);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }

            return true;
        }, _ => true, cancellationToken).ConfigureAwait(false);
    }
}
