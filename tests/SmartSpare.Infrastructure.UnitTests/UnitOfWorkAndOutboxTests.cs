using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>
/// Test double for a module unit of work: records the transaction decisions and keeps the inbox
/// in memory. Scoped, like the real one.
/// </summary>
internal sealed class RecordingUnitOfWork : ITransactionalUnitOfWork
{
    public static HashSet<(Guid, string)> Inbox { get; } = [];

    public List<IIntegrationEvent> Events { get; } = [];

    public List<string> Outcomes { get; } = [];

    public void AddIntegrationEvent(IIntegrationEvent integrationEvent) => Events.Add(integrationEvent);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work, Func<TResult, bool> commit, CancellationToken cancellationToken)
    {
        try
        {
            var result = await work(cancellationToken);
            Outcomes.Add(commit(result) ? "commit" : "rollback");
            return result;
        }
        catch
        {
            Outcomes.Add("rollback");
            throw;
        }
    }

    public Task<bool> TryAcceptInboxMessageAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
    {
        lock (Inbox)
        {
            return Task.FromResult(Inbox.Add((messageId, consumer)));
        }
    }
}

public sealed class TransactionDecoratorTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHandlersFromAssembly(typeof(TransactionDecoratorTests).Assembly);
        services.AddUnitOfWork<RecordingUnitOfWork>(typeof(TransactionDecoratorTests).Assembly);
        services.DecorateHandlers();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Theory]
    [InlineData(true, "commit")]
    [InlineData(false, "rollback")]
    public async Task Command_CommitsOnSuccessAndRollsBackOnFailure(bool succeed, string expected)
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<Probe>>();

        await handler.Handle(new Probe(succeed, Throw: false), TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<RecordingUnitOfWork>().Outcomes.ShouldBe([expected]);
    }

    [Fact]
    public async Task Exception_RollsBackAndPropagates()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<Probe>>();

        await Should.ThrowAsync<InvalidOperationException>(() => handler.Handle(new Probe(true, Throw: true), TestContext.Current.CancellationToken));

        scope.ServiceProvider.GetRequiredService<RecordingUnitOfWork>().Outcomes.ShouldBe(["rollback"]);
    }

    [Fact]
    public void Commands_WithoutAUnitOfWork_FailAtStartUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHandlersFromAssembly(typeof(TransactionDecoratorTests).Assembly);

        Should.Throw<InvalidOperationException>(() => services.DecorateHandlers())
            .Message.ShouldContain("SmartSpare.Infrastructure.UnitTests");
    }

    internal sealed record Probe(bool Succeed, bool Throw) : ICommand;

    internal sealed class ProbeHandler : ICommandHandler<Probe>
    {
        public Task<Result> Handle(Probe command, CancellationToken cancellationToken) =>
            command.Throw
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(command.Succeed ? Result.Success() : Result.Failure(Error.Conflict("Probe.Failed", "failed")));
    }
}

public sealed class OutboxProcessorTests
{
    private static OutboxEnvelope Envelope() => new(Guid.NewGuid(), "T, A", "{}");

    [Fact]
    public async Task EveryMessage_IsAttempted_AndFailuresDoNotStopTheBatch()
    {
        var batch = new[] { Envelope(), Envelope(), Envelope() };
        var seen = new List<Guid>();

        var result = await OutboxProcessor.ProcessAsync(batch, (m, _) =>
        {
            seen.Add(m.Id);
            return m == batch[1] ? throw new InvalidOperationException("consumer down") : Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        seen.ShouldBe(batch.Select(b => b.Id));
        result.Delivered.ShouldBe([batch[0].Id, batch[2].Id]);
        result.Failed.ShouldHaveSingleItem().ShouldBe((batch[1].Id, "consumer down"));
    }

    [Fact]
    public async Task LongErrors_AreTruncatedToTheColumnSize()
    {
        var result = await OutboxProcessor.ProcessAsync([Envelope()],
            (_, _) => throw new InvalidOperationException(new string('x', 5000)), TestContext.Current.CancellationToken);

        result.Failed.Single().Error.Length.ShouldBe(OutboxModelBuilderExtensions.MaxErrorLength);
    }

    [Fact]
    public async Task Cancellation_IsNotRecordedAsAFailure() =>
        await Should.ThrowAsync<OperationCanceledException>(() => OutboxProcessor.ProcessAsync([Envelope()],
            (_, _) => throw new OperationCanceledException(), TestContext.Current.CancellationToken));
}

public sealed class InProcessPublisherTests
{
    [Fact]
    public async Task EveryConsumer_HandlesTheEventOnce_EvenWhenItIsDeliveredTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHandlersFromAssembly(typeof(InProcessPublisherTests).Assembly);
        services.AddUnitOfWork<RecordingUnitOfWork>(typeof(InProcessPublisherTests).Assembly);
        services.AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IIntegrationEventPublisher>();
        var message = IntegrationEventSerializer.ToOutboxMessage(new PartRenamed(42, "Oil filter"));
        var envelope = new OutboxEnvelope(message.Id, message.Type, message.Payload);
        CountingConsumers.Calls.Clear();

        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);

        CountingConsumers.Calls.ShouldBe(["first:42:Oil filter", "second:42:Oil filter"], ignoreOrder: true);
    }

    [Fact]
    public void Serializer_RoundTripsTheEvent()
    {
        var original = new PartRenamed(7, "Belt") { OccurredAt = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero) };
        var message = IntegrationEventSerializer.ToOutboxMessage(original);

        message.Type.ShouldBe($"{typeof(PartRenamed).FullName}, {typeof(PartRenamed).Assembly.GetName().Name}");
        IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBe(original);
    }

    internal sealed record PartRenamed(long PartId, string Name) : IntegrationEvent;

    internal static class CountingConsumers
    {
        public static List<string> Calls { get; } = [];
    }

    [IntegrationEventConsumer("tests.first")]
    internal sealed class FirstConsumer : IIntegrationEventConsumer<PartRenamed>
    {
        public Task Handle(PartRenamed integrationEvent, CancellationToken cancellationToken)
        {
            CountingConsumers.Calls.Add($"first:{integrationEvent.PartId}:{integrationEvent.Name}");
            return Task.CompletedTask;
        }
    }

    [IntegrationEventConsumer("tests.second")]
    internal sealed class SecondConsumer : IIntegrationEventConsumer<PartRenamed>
    {
        public Task Handle(PartRenamed integrationEvent, CancellationToken cancellationToken)
        {
            CountingConsumers.Calls.Add($"second:{integrationEvent.PartId}:{integrationEvent.Name}");
            return Task.CompletedTask;
        }
    }
}
