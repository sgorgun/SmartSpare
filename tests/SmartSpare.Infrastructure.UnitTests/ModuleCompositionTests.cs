using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>The shared composition of modules in every host (ADR 0015 §1, STAGE_4_PLAN 1.2).</summary>
public sealed class ModuleCompositionTests
{
    private static IConfiguration WithDatabase() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PersistenceRegistration.ConnectionStringName}"] = "Host=localhost;Database=unused",
        })
        .Build();

    private static IConfiguration WithoutDatabase() => new ConfigurationBuilder().Build();

    [Fact]
    public void The_single_host_delivers_integration_events_in_process()
    {
        var services = new ServiceCollection();

        services.AddModules(WithDatabase(), [new ProbeModule()], Topology.SingleHost);

        services.ShouldContain(d => d.ServiceType == typeof(IIntegrationEventPublisher)
                                    && d.ImplementationType == typeof(InProcessIntegrationEventPublisher));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
    }

    [Fact]
    public void A_service_host_must_register_its_transport_first()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddModules(WithDatabase(), [new ProbeModule()], Topology.Services))
            .Message.ShouldContain("RabbitMQ");
    }

    [Fact]
    public void A_service_host_keeps_its_own_transport()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventPublisher, NoTransport>();

        services.AddModules(WithDatabase(), [new ProbeModule()], Topology.Services);

        services.Where(d => d.ServiceType == typeof(IIntegrationEventPublisher))
            .ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(NoTransport));
    }

    [Fact]
    public void Without_a_database_there_is_no_outbox_to_drain()
    {
        var services = new ServiceCollection();

        services.AddModules(WithoutDatabase(), [new ProbeModule()], Topology.Services);

        services.ShouldNotContain(d => d.ImplementationType == typeof(OutboxDispatcher));
    }

    [Fact]
    public void Every_module_is_installed_and_consumer_names_are_checked()
    {
        var services = new ServiceCollection();
        var unnamed = new ProbeModule(s => s.AddScoped<IIntegrationEventConsumer<ConsumerNameTests.PartMoved>, UnnamedConsumer>());

        Should.Throw<InvalidOperationException>(() => services.AddModules(WithoutDatabase(), [new ProbeModule(), unnamed], Topology.SingleHost))
            .Message.ShouldContain("#32");
    }

    [Fact]
    public async Task Module_endpoints_get_the_host_conventions()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using var app = builder.Build();
        var conventionApplied = false;

        app.MapModules([new ProbeModule()], group => group.AddEndpointFilter((context, next) =>
        {
            conventionApplied = true;
            return next(context);
        }));
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ShouldHaveSingleItem();
        endpoint.RoutePattern.RawText.ShouldBe("/api/probe/ping");

        var context = new DefaultHttpContext { RequestServices = app.Services };
        await endpoint.RequestDelegate!(context);
        conventionApplied.ShouldBeTrue();
    }

    private sealed class ProbeModule(Action<IServiceCollection>? register = null) : IModuleInstaller
    {
        public string Name => "probe";

        public void AddServices(IServiceCollection services, IConfiguration configuration) => register?.Invoke(services);

        public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/api/probe/ping", () => "ok");
    }

    private sealed class NoTransport : IIntegrationEventPublisher
    {
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnnamedConsumer : IIntegrationEventConsumer<ConsumerNameTests.PartMoved>
    {
        public Task Handle(ConsumerNameTests.PartMoved integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
