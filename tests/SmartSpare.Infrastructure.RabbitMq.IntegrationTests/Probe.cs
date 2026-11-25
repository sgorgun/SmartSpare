using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

// Test events and consumers of a "probe" module: the transport derives exchange and queue names from the module
// namespace and the consumer name, as it does for real modules.
namespace SmartSpare.Modules.Probe.Contracts
{
    public sealed record PartCounted(int PartId) : IntegrationEvent;

    public sealed record PartCountedV2(int PartId, int Quantity) : IntegrationEvent;

    /// <summary>Published by a test, consumed by nobody: no queue is bound for it.</summary>
    public sealed record PartForgotten(int PartId) : IntegrationEvent;

    public sealed record PartBroken(int PartId) : IntegrationEvent;
}

namespace SmartSpare.Infrastructure.RabbitMq.IntegrationTests
{
    using SmartSpare.Modules.Probe.Contracts;

    /// <summary>What the probe consumers saw, shared by the tests of one collection (they run one at a time).</summary>
    internal static class Received
    {
        public static List<(int PartId, string? TraceId)> Counted { get; } = [];

        public static int BrokenAttempts;

        public static void Clear()
        {
            lock (Counted)
            {
                Counted.Clear();
            }

            BrokenAttempts = 0;
        }
    }

    [IntegrationEventConsumer("probe.count-parts")]
    internal sealed class CountParts : IIntegrationEventConsumer<PartCounted>
    {
        public Task Handle(PartCounted integrationEvent, CancellationToken cancellationToken)
        {
            lock (Received.Counted)
            {
                Received.Counted.Add((integrationEvent.PartId, System.Diagnostics.Activity.Current?.TraceId.ToString()));
            }

            return Task.CompletedTask;
        }
    }

    [IntegrationEventConsumer("probe.always-fails")]
    internal sealed class AlwaysFails : IIntegrationEventConsumer<PartBroken>
    {
        public Task Handle(PartBroken integrationEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Received.BrokenAttempts);
            throw new InvalidOperationException("the consumer is broken");
        }
    }

    /// <summary>
    /// The probe module's unit of work: an inbox in memory with real transaction semantics, so an inbox row written by a
    /// consumer that then throws is rolled back, as in PostgreSQL, and the redelivery is handled again.
    /// </summary>
    internal sealed class InMemoryUnitOfWork : ITransactionalUnitOfWork
    {
        private readonly List<(Guid, string)> _pending = [];

        public static HashSet<(Guid, string)> Inbox { get; } = [];

        public void AddIntegrationEvent(IIntegrationEvent integrationEvent) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<TResult> ExecuteInTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> work, Func<TResult, bool> commit, CancellationToken cancellationToken)
        {
            _pending.Clear();
            var result = await work(cancellationToken);
            if (commit(result))
            {
                lock (Inbox)
                {
                    Inbox.UnionWith(_pending);
                }
            }

            return result;      // an exception leaves _pending uncommitted, like a rollback
        }

        public Task<bool> TryAcceptInboxMessageAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
        {
            lock (Inbox)
            {
                if (Inbox.Contains((messageId, consumer)) || _pending.Contains((messageId, consumer)))
                {
                    return Task.FromResult(false);
                }
            }

            _pending.Add((messageId, consumer));
            return Task.FromResult(true);
        }
    }
}
