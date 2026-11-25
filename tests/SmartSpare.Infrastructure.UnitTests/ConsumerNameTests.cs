using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>Stable integration event consumer names (#32): the inbox key survives renaming the consumer class.</summary>
public sealed class ConsumerNameTests
{
    [Fact]
    public async Task Renaming_a_consumer_class_does_not_handle_an_already_handled_event_again()
    {
        var message = IntegrationEventSerializer.ToOutboxMessage(new PartMoved(7));
        var envelope = new OutboxEnvelope(message.Id, message.Type, message.Payload);
        Calls.Clear();

        // Before the refactoring: the event is handled by ConsumerBeforeRename.
        await DeliverAsync<ConsumerBeforeRename>(envelope);
        // After it: the class is now ConsumerAfterRename, with the same declared name; the redelivered event is skipped.
        await DeliverAsync<ConsumerAfterRename>(envelope);

        Calls.ShouldBe(["before:7"]);
        RecordingUnitOfWork.Inbox.ShouldContain((message.Id, "tests.count-moves"));
    }

    [Fact]
    public void A_consumer_without_a_name_fails_start_up()
    {
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventConsumer<PartMoved>, UnnamedConsumer>();

        Should.Throw<InvalidOperationException>(() => IntegrationEventConsumers.Validate(services))
            .Message.ShouldContain(nameof(UnnamedConsumer));
    }

    [Fact]
    public void Two_consumers_with_one_name_fail_start_up()
    {
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventConsumer<PartMoved>, ConsumerBeforeRename>();
        services.AddScoped<IIntegrationEventConsumer<PartMoved>, ConsumerAfterRename>();

        Should.Throw<InvalidOperationException>(() => IntegrationEventConsumers.Validate(services))
            .Message.ShouldContain("'tests.count-moves'");
    }

    [Fact]
    public void One_consumer_handling_two_events_is_one_name()
    {
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventConsumer<PartMoved>, TwoEventConsumer>();
        services.AddScoped<IIntegrationEventConsumer<PartRetired>, TwoEventConsumer>();

        Should.NotThrow(() => IntegrationEventConsumers.Validate(services));
    }

    [Theory]
    [InlineData("inventory.reserve-service-kit", true)]
    [InlineData("forecasting.transfer-approved", true)]
    [InlineData("fleet.kit2", true)]
    [InlineData("Inventory.reserve", false)]       // upper case
    [InlineData("inventory", false)]               // no purpose
    [InlineData("inventory.reserve_kit", false)]   // underscore
    [InlineData("inventory.-reserve", false)]      // leading hyphen
    [InlineData("inventory.reserve-", false)]      // trailing hyphen
    [InlineData("inventory.reserve.kit", false)]   // two dots
    [InlineData("", false)]
    public void Names_are_module_dot_kebab_case(string name, bool valid) =>
        IntegrationEventConsumers.IsValidName(name).ShouldBe(valid);

    [Fact]
    public void Names_longer_than_the_limit_are_rejected() =>
        IntegrationEventConsumers.IsValidName("inventory." + new string('a', IntegrationEventConsumers.MaxNameLength)).ShouldBeFalse();

    private static async Task DeliverAsync<TConsumer>(OutboxEnvelope envelope)
        where TConsumer : class, IIntegrationEventConsumer<PartMoved>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IIntegrationEventConsumer<PartMoved>, TConsumer>();
        services.AddUnitOfWork<RecordingUnitOfWork>(typeof(ConsumerNameTests).Assembly);
        services.AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
        IntegrationEventConsumers.Validate(services);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(envelope, TestContext.Current.CancellationToken);
    }

    private static List<string> Calls { get; } = [];

    internal sealed record PartMoved(long PartId) : IntegrationEvent;

    internal sealed record PartRetired(long PartId) : IntegrationEvent;

    [IntegrationEventConsumer("tests.count-moves")]
    internal sealed class ConsumerBeforeRename : IIntegrationEventConsumer<PartMoved>
    {
        public Task Handle(PartMoved integrationEvent, CancellationToken cancellationToken)
        {
            Calls.Add($"before:{integrationEvent.PartId}");
            return Task.CompletedTask;
        }
    }

    [IntegrationEventConsumer("tests.count-moves")]
    internal sealed class ConsumerAfterRename : IIntegrationEventConsumer<PartMoved>
    {
        public Task Handle(PartMoved integrationEvent, CancellationToken cancellationToken)
        {
            Calls.Add($"after:{integrationEvent.PartId}");
            return Task.CompletedTask;
        }
    }

    [IntegrationEventConsumer("tests.two-events")]
    internal sealed class TwoEventConsumer : IIntegrationEventConsumer<PartMoved>, IIntegrationEventConsumer<PartRetired>
    {
        public Task Handle(PartMoved integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Handle(PartRetired integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class UnnamedConsumer : IIntegrationEventConsumer<PartMoved>
    {
        public Task Handle(PartMoved integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
