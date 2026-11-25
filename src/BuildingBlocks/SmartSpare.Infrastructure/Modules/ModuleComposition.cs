using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.Modules;

/// <summary>Which composition root runs the modules (ADR 0015 §1). Module code never sees it.</summary>
public enum Topology
{
    /// <summary><c>SmartSpare.Host</c>: all modules in one process, integration events delivered in process.</summary>
    SingleHost,

    /// <summary>One <c>SmartSpare.Services.&lt;Module&gt;</c> per module: integration events through RabbitMQ (ADR 0017).</summary>
    Services,
}

/// <summary>
/// Installs modules the same way in every host (ADR 0015 §1), so the single host, the service hosts and the tests
/// can't drift apart in how handlers are decorated, consumers named, outboxes drained or users resolved.
/// </summary>
public static class ModuleComposition
{
    /// <summary>
    /// Registers <paramref name="modules"/> and the pipeline they share: handler decorators (once, after every module),
    /// the consumer-name check (#32), outbox dispatch when a database is configured, and the claims-based current user.
    /// </summary>
    /// <remarks>
    /// In <see cref="Topology.SingleHost"/> the outbox delivers in process. In <see cref="Topology.Services"/> the host
    /// must register its transport (<see cref="IIntegrationEventPublisher"/>) before calling this method.
    /// </remarks>
    public static IServiceCollection AddModules(
        this IServiceCollection services, IConfiguration configuration, IReadOnlyList<IModuleInstaller> modules, Topology topology)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
        }

        // Once, after all modules: decorating per module would wrap earlier modules' handlers twice.
        services.DecorateHandlers();
        IntegrationEventConsumers.Validate(services);

        // Without a database (in-memory host tests) there is no outbox to drain.
        if (configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName) is not null)
        {
            if (topology == Topology.Services && !services.Any(d => d.ServiceType == typeof(IIntegrationEventPublisher)))
            {
                throw new InvalidOperationException(
                    "A service host publishes integration events through the RabbitMQ transport (ADR 0017): register it before AddModules.");
            }

            services.AddOutboxDispatcher();
        }

        // The session context of every module transaction comes from the request's claims (#11).
        services.AddClaimsCurrentUser();
        return services;
    }

    /// <summary>
    /// Maps every module's endpoints into one group without a prefix (modules keep their own <c>/api/{module}</c> routes).
    /// <paramref name="configureGroup"/> adds the host's conventions first, for example antiforgery where the cookie is accepted.
    /// </summary>
    public static RouteGroupBuilder MapModules(
        this IEndpointRouteBuilder endpoints, IEnumerable<IModuleInstaller> modules, Action<RouteGroupBuilder>? configureGroup = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(modules);

        var group = endpoints.MapGroup(string.Empty);
        configureGroup?.Invoke(group);
        foreach (var module in modules)
        {
            module.MapEndpoints(group);
        }

        return group;
    }
}
