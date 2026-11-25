using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// The stable name of an integration event consumer (#32), for example <c>inventory.reserve-service-kit</c>. The inbox
/// keys handled events by <c>(message id, consumer name)</c>, and the RabbitMQ transport derives queue bindings from it,
/// so renaming, moving or re-namespacing the class never makes an already handled event look new.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class IntegrationEventConsumerAttribute(string name) : Attribute
{
    /// <summary><c>&lt;module&gt;.&lt;purpose&gt;</c>, lower-case, the purpose in kebab case.</summary>
    public string Name { get; } = name;
}

/// <summary>Reads and validates consumer names.</summary>
public static partial class IntegrationEventConsumers
{
    /// <summary>Longest name that fits <c>inbox_message.consumer</c> together with room to spare for future prefixes.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The declared name of a consumer type.</summary>
    /// <exception cref="InvalidOperationException">The type declares no name, or an invalid one.</exception>
    public static string NameOf(Type consumerType)
    {
        ArgumentNullException.ThrowIfNull(consumerType);
        var name = consumerType.GetCustomAttribute<IntegrationEventConsumerAttribute>()?.Name
            ?? throw new InvalidOperationException(
                $"Integration event consumer {consumerType.FullName} has no [IntegrationEventConsumer(\"<module>.<purpose>\")] name (#32).");
        return IsValidName(name)
            ? name
            : throw new InvalidOperationException(
                $"Integration event consumer {consumerType.FullName} has the invalid name '{name}'; use '<module>.<purpose>' in lower case, " +
                $"for example 'inventory.reserve-service-kit', at most {MaxNameLength} characters.");
    }

    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && NamePattern().IsMatch(name);

    /// <summary>
    /// Fails start-up when a registered consumer has no valid name, or when two consumer types share one, which would make
    /// one of them skip events the other already handled.
    /// </summary>
    public static void Validate(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var consumerTypes = services
            .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventConsumer<>))
            .Select(d => d.ImplementationType ?? throw new InvalidOperationException(
                $"Integration event consumers must be registered by type, not by factory or instance ({d.ServiceType})."))
            .Distinct();

        var duplicates = consumerTypes
            .GroupBy(NameOf, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}': {string.Join(", ", g.Select(t => t.FullName))}")
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException("Integration event consumer names must be unique (#32): " + string.Join("; ", duplicates));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*\\.[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
