using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Probe.Contracts;

namespace SmartSpare.Infrastructure.RabbitMq.IntegrationTests;

/// <summary>Exchange, routing key and queue names of ADR 0017 §2. No broker needed.</summary>
public sealed class TopologyNamingTests
{
    [Fact]
    public void The_exchange_belongs_to_the_publishing_module()
    {
        IntegrationEventTopology.PublishingModule(typeof(PartCounted)).ShouldBe("probe");
        IntegrationEventTopology.Exchange(typeof(PartCounted)).ShouldBe("smartspare.probe.events");
    }

    [Fact]
    public void The_routing_key_is_the_event_name_and_version()
    {
        IntegrationEventTopology.RoutingKey(typeof(PartCounted)).ShouldBe("PartCounted.v1");
        IntegrationEventTopology.RoutingKey(typeof(PartCountedV2)).ShouldBe("PartCounted.v2");
    }

    [Fact]
    public void A_queue_is_one_consumer_of_one_event_and_starts_with_the_consuming_module()
    {
        IntegrationEventTopology.Queue("inventory.reserve-service-kit", typeof(PartCounted)).ShouldBe("inventory.reserve-service-kit.PartCounted.v1");
        IntegrationEventTopology.ConsumingModule("inventory.reserve-service-kit").ShouldBe("inventory");
        IntegrationEventTopology.DeadLetterExchange("inventory").ShouldBe("smartspare.inventory.dlx");
        IntegrationEventTopology.DeadLetterQueue("inventory").ShouldBe("inventory.dead-letter");
    }

    [Fact]
    public void An_event_outside_a_module_namespace_has_no_exchange() =>
        Should.Throw<InvalidOperationException>(() => IntegrationEventTopology.Exchange(typeof(NotInAModule)));

    [Fact]
    public void Subscriptions_come_from_the_registered_consumers()
    {
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventConsumer<PartCounted>, CountParts>();
        services.AddScoped<IIntegrationEventConsumer<PartBroken>, AlwaysFails>();

        var subscriptions = IntegrationEventSubscription.From(services);

        subscriptions.Select(s => s.Queue).ShouldBe(["probe.always-fails.PartBroken.v1", "probe.count-parts.PartCounted.v1"]);
        subscriptions.ShouldAllBe(s => s.Exchange == "smartspare.probe.events" && s.ConsumingModule == "probe");
    }

    private sealed record NotInAModule : SmartSpare.SharedKernel.IntegrationEvent;
}
