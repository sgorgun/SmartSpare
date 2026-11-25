using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Probe.Contracts;
using SmartSpare.SharedKernel;
using Testcontainers.RabbitMq;

namespace SmartSpare.Infrastructure.RabbitMq.IntegrationTests;

/// <summary>One RabbitMQ 4.4 broker for the transport tests.</summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4.4").Build();

    public string ConnectionString => _broker.GetConnectionString();

    public ValueTask InitializeAsync() => new(_broker.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => _broker.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class BrokerGroup : ICollectionFixture<BrokerFixture>
{
    public const string Name = "RabbitMQ broker";
}

/// <summary>
/// The RabbitMQ transport against a real broker (ADR 0017 §2–§3): confirmed publish and consume, idempotent redelivery,
/// unroutable publishes, dead-lettering after the delivery limit, idempotent declarations and the trace across the broker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(BrokerGroup.Name)]
public sealed class RabbitMqTransportTests(BrokerFixture broker)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_published_event_is_consumed_once_and_acknowledged()
    {
        Received.Clear();
        await using var service = await StartAsync(typeof(CountParts));
        var publisher = service.GetRequiredService<IIntegrationEventPublisher>();
        var envelope = Envelope(new PartCounted(7));

        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);
        // Redelivery of the same event (an outbox retry after a lost confirm): the inbox skips it.
        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);

        await Until(() => QueueDepthAsync("probe.count-parts.PartCounted.v1").Result == 0 && Received.Counted.Count >= 1);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Received.Counted.Select(c => c.PartId).ShouldBe([7]);
        InMemoryUnitOfWork.Inbox.ShouldContain((envelope.Id, "probe.count-parts"));
    }

    [Fact]
    public async Task An_event_nobody_consumes_is_returned_and_stays_in_the_outbox()
    {
        await using var service = await StartAsync();
        var publisher = service.GetRequiredService<IIntegrationEventPublisher>();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(Envelope(new PartForgotten(1)), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("No queue is bound for PartForgotten.v1 on smartspare.probe.events");
    }

    [Fact]
    public async Task A_failing_consumer_is_dead_lettered_after_the_delivery_limit()
    {
        Received.Clear();
        await using var service = await StartAsync([typeof(AlwaysFails)], deliveryLimit: 2);
        var before = await QueueDepthAsync("probe.dead-letter");

        await service.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(Envelope(new PartBroken(3)), TestContext.Current.CancellationToken);

        await Until(() => QueueDepthAsync("probe.dead-letter").Result == before + 1,
            () => $"attempts {Received.BrokenAttempts}, queue {QueueDepthAsync("probe.always-fails.PartBroken.v1").Result}, dead letters {QueueDepthAsync("probe.dead-letter").Result}");
        Received.BrokenAttempts.ShouldBeGreaterThanOrEqualTo(2);
        (await QueueDepthAsync("probe.always-fails.PartBroken.v1")).ShouldBe(0u);
    }

    [Fact]
    public async Task Declarations_are_idempotent_across_restarts()
    {
        await using (await StartAsync(typeof(CountParts)))
        {
        }

        await using var again = await StartAsync(typeof(CountParts));
        (await QueueDepthAsync("probe.count-parts.PartCounted.v1")).ShouldBe(0u);
    }

    [Fact]
    public async Task The_trace_continues_from_the_publisher_to_the_consumer()
    {
        Received.Clear();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is IntegrationEventDelivery.ActivitySourceName or "Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var tests = new ActivitySource("Tests");
        await using var service = await StartAsync(typeof(CountParts));

        string traceId;
        using (var request = tests.StartActivity("request"))
        {
            traceId = request!.TraceId.ToString();
            await service.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(Envelope(new PartCounted(11)), TestContext.Current.CancellationToken);
        }

        await Until(() => Received.Counted.Any(c => c.PartId == 11));
        Received.Counted.Single(c => c.PartId == 11).TraceId.ShouldBe(traceId);
    }

    private static OutboxEnvelope Envelope(IIntegrationEvent integrationEvent)
    {
        var message = IntegrationEventSerializer.ToOutboxMessage(integrationEvent);
        return new OutboxEnvelope(message.Id, message.Type, message.Payload);
    }

    private Task<ServiceProvider> StartAsync(params Type[] consumers) => StartAsync(consumers, deliveryLimit: 10);

    private async Task<ServiceProvider> StartAsync(Type[] consumers, int deliveryLimit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{RabbitMqOptions.ConnectionStringName}"] = broker.ConnectionString,
            [$"{RabbitMqOptions.Section}:DeliveryLimit"] = deliveryLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUnitOfWork<InMemoryUnitOfWork>(typeof(CountParts).Assembly);
        foreach (var consumer in consumers)
        {
            var eventType = consumer.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventConsumer<>));
            services.AddScoped(eventType, consumer);
        }

        services.AddRabbitMqTransport(configuration, "probe-tests");
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var subscriber = provider.GetRequiredService<RabbitMqSubscriber>();
        await subscriber.StartAsync(TestContext.Current.CancellationToken);
        await subscriber.Ready.WaitAsync(Patience, TestContext.Current.CancellationToken);
        return provider;
    }

    private async Task<uint> QueueDepthAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(broker.ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken)).MessageCount;
    }

    private static async Task Until(Func<bool> condition, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The condition was not met within {Patience.TotalSeconds} s. {state?.Invoke()}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
