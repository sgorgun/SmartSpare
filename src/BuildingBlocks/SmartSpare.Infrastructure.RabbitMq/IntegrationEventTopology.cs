using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>
/// The naming of ADR 0017 §2:
/// <list type="bullet">
/// <item>one durable topic exchange per publishing module, <c>smartspare.&lt;module&gt;.events</c>;</item>
/// <item>routing key <c>&lt;EventName&gt;.v&lt;N&gt;</c>; a type named <c>TransferApprovedV2</c> is version 2, otherwise 1;</item>
/// <item>one durable quorum queue per subscription, <c>&lt;consumer name&gt;.&lt;EventName&gt;.v&lt;N&gt;</c>. The consumer name
/// starts with the consuming module (#32), so the queue does too, and two consumers of one module never share a queue;</item>
/// <item>one dead-letter exchange and queue per consuming module, <c>smartspare.&lt;module&gt;.dlx</c> → <c>&lt;module&gt;.dead-letter</c>.</item>
/// </list>
/// </summary>
public static partial class IntegrationEventTopology
{
    /// <summary>The module that publishes <paramref name="eventType"/>: from its namespace, <c>SmartSpare.Modules.&lt;Module&gt;…</c>.</summary>
    public static string PublishingModule(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        var match = ModuleNamespace().Match(eventType.Namespace ?? string.Empty);
        return match.Success
            ? match.Groups["module"].Value.ToLowerInvariant()
            : throw new InvalidOperationException(
                $"Integration event {eventType.FullName} is not in a module namespace (SmartSpare.Modules.<Module>...), so it has no exchange.");
    }

    public static string Exchange(string module) => $"smartspare.{module}.events";

    public static string Exchange(Type eventType) => Exchange(PublishingModule(eventType));

    /// <summary><c>&lt;EventName&gt;.v&lt;N&gt;</c>.</summary>
    public static string RoutingKey(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        var match = VersionSuffix().Match(eventType.Name);
        return match.Success
            ? $"{match.Groups["name"].Value}.v{match.Groups["version"].Value}"
            : $"{eventType.Name}.v1";
    }

    public static string Queue(string consumerName, Type eventType) => $"{consumerName}.{RoutingKey(eventType)}";

    /// <summary>The consuming module: the first part of the consumer's stable name.</summary>
    public static string ConsumingModule(string consumerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerName);
        return consumerName[..consumerName.IndexOf('.', StringComparison.Ordinal)];
    }

    public static string DeadLetterExchange(string module) => $"smartspare.{module}.dlx";

    public static string DeadLetterQueue(string module) => $"{module}.dead-letter";

    [GeneratedRegex(@"^SmartSpare\.Modules\.(?<module>[A-Za-z]+)(\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleNamespace();

    [GeneratedRegex(@"^(?<name>.+?)V(?<version>[2-9]|[1-9][0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffix();
}

/// <summary>One consumer of one event type: one queue (ADR 0017 §2).</summary>
public sealed record IntegrationEventSubscription(string ConsumerName, Type ConsumerType, Type EventType)
{
    public Type ServiceType => typeof(IIntegrationEventConsumer<>).MakeGenericType(EventType);

    public string Queue => IntegrationEventTopology.Queue(ConsumerName, EventType);

    public string Exchange => IntegrationEventTopology.Exchange(EventType);

    public string RoutingKey => IntegrationEventTopology.RoutingKey(EventType);

    public string ConsumingModule => IntegrationEventTopology.ConsumingModule(ConsumerName);

    /// <summary>Every consumer registered in <paramref name="services"/>, one subscription per consumed event type.</summary>
    public static IReadOnlyList<IntegrationEventSubscription> From(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return
        [
            .. services
                .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventConsumer<>)
                            && d.ImplementationType is not null)
                .Select(d => new IntegrationEventSubscription(
                    IntegrationEventConsumers.NameOf(d.ImplementationType!), d.ImplementationType!, d.ServiceType.GetGenericArguments()[0]))
                .Distinct()
                .OrderBy(s => s.Queue, StringComparer.Ordinal),
        ];
    }
}
