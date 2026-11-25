using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// Modules are identical in both topologies (ADR 0015 §1): no module knows the gateway, the broker or the host that
/// composes it, and every integration event consumer has a stable name (#32).
/// </summary>
public sealed class TopologyBoundaryTests
{
    /// <summary>Packages that belong to a composition root or a transport, never to a module (ADR 0015 §1, ADR 0017 §1).</summary>
    private static readonly string[] TopologyPackages = ["Yarp.ReverseProxy", "Microsoft.Extensions.ServiceDiscovery.Yarp", "RabbitMQ.Client"];

    /// <summary>SmartSpare assemblies that are topology concerns: the transport, the BFF and every host.</summary>
    private static bool IsTopologyAssembly(string name) =>
        name is "SmartSpare.Infrastructure.RabbitMq" or "SmartSpare.Bff" or "SmartSpare.Host" or "SmartSpare.Gateway"
            or "SmartSpare.MigrationRunner" or "SmartSpare.AppHost"
        || name.StartsWith("SmartSpare.Services.", StringComparison.Ordinal);

    private static bool IsTopologyPackage(string name) =>
        TopologyPackages.Any(p => name.Equals(p, StringComparison.Ordinal) || name.StartsWith(p + ".", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Module_ReferencesNoTopologyConcern(string name)
    {
        var module = Solution.Get(name);

        foreach (var assembly in new[] { module.Implementation, module.Contracts })
        {
            var project = assembly.GetName().Name!;
            Solution.PackageReferences(project).ShouldNotContain(p => IsTopologyPackage(p), project);
            Solution.ProjectReferences(project).ShouldNotContain(r => IsTopologyAssembly(r), project);
            assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .ShouldNotContain(r => IsTopologyPackage(r) || IsTopologyAssembly(r), project);
        }
    }

    [Fact]
    public void Infrastructure_KeepsTheBrokerAndTheGatewayOutOfModules()
    {
        // Every module references SmartSpare.Infrastructure, so a transport package there would reach every module.
        Solution.PackageReferences("SmartSpare.Infrastructure").ShouldNotContain(p => IsTopologyPackage(p));
        typeof(IIntegrationEventConsumer<>).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
            .ShouldNotContain(r => IsTopologyPackage(r) || IsTopologyAssembly(r));
    }

    /// <summary>
    /// The free broker allows 100 queues (ADR 0017 §5): at most 45 subscriptions (one queue per consumer and event,
    /// ADR 0021), leaving room for the six dead-letter queues and one retry queue per subscription if the fallback is needed.
    /// </summary>
    [Fact]
    public void Subscriptions_stay_within_the_broker_queue_budget()
    {
        const int Budget = 45;
        var subscriptions = Solution.Modules
            .SelectMany(m => m.Implementation.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventConsumer<>))
                .Select(i => (Consumer: t, Event: i.GetGenericArguments()[0])))
            .ToList();

        subscriptions.ShouldNotBeEmpty("the diagnostics probe of stage 4 is a subscription; an empty list would mean the scan is broken");
        subscriptions.Count.ShouldBeLessThanOrEqualTo(Budget, string.Join(", ", subscriptions.Select(s => $"{s.Consumer.Name}<{s.Event.Name}>")));
    }

    [Fact]
    public void Every_integration_event_consumer_has_a_unique_stable_name()
    {
        var consumers = Solution.Modules
            .SelectMany(m => m.Implementation.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventConsumer<>)))
            .ToList();

        // NameOf throws for a missing or malformed name.
        var names = consumers.Select(t => (Type: t, Name: IntegrationEventConsumers.NameOf(t))).ToList();
        names.GroupBy(n => n.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
        names.ShouldAllBe(n => n.Name.StartsWith(Solution.ModuleOf(n.Type) + ".", StringComparison.Ordinal),
            "a consumer's name starts with its own module, e.g. 'inventory.reserve-service-kit'");
    }
}
