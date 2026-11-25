using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure.RabbitMq;

public static class RabbitMqTransportRegistration
{
    /// <summary>
    /// The RabbitMQ transport of a service host (ADR 0017): the outbox publishes through RabbitMQ, and the subscriber
    /// consumes the queues of every consumer the host's modules register. Call before <c>AddModules</c> with
    /// <c>Topology.Services</c>. The URL is the connection string <c>messaging</c> (user-secrets, Aspire or Key Vault).
    /// </summary>
    public static IServiceCollection AddRabbitMqTransport(this IServiceCollection services, IConfiguration configuration, string clientName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.Section))
            .Configure(o =>
            {
                o.ConnectionString ??= configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName);
                o.ClientName = clientName;
            })
            .Validate(o => Uri.TryCreate(o.ConnectionString, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps",
                $"The RabbitMQ transport needs an amqp:// or amqps:// URL in the connection string '{RabbitMqOptions.ConnectionStringName}'.")
            .ValidateOnStart();

        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IIntegrationEventPublisher, RabbitMqIntegrationEventPublisher>();
        // Evaluated when first resolved, after every module has registered its consumers.
        services.AddSingleton(_ => new RabbitMqSubscriptions(IntegrationEventSubscription.From(services)));
        services.AddSingleton<RabbitMqSubscriber>();
        services.AddHostedService(sp => sp.GetRequiredService<RabbitMqSubscriber>());
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
        return services;
    }
}

/// <summary>
/// Readiness check of the broker connection: Degraded, not Unhealthy, while it is closed. HTTP still works and the outbox
/// keeps every event until the broker is back, so the service stays in rotation.
/// </summary>
internal sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(connection.IsOpen
            ? HealthCheckResult.Healthy("Connected to RabbitMQ.")
            : HealthCheckResult.Degraded("Not connected to RabbitMQ; integration events wait in the outbox."));
}
