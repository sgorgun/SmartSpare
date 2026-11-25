using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>Handles an integration event in the consuming module (in-process in the single host, RabbitMQ between services; ADR 0017).</summary>
public interface IIntegrationEventConsumer<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task Handle(TEvent integrationEvent, CancellationToken cancellationToken);
}

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 50;

    /// <summary>After this many failed attempts a message is parked (<c>exhausted_at</c>) for an operator (#31).</summary>
    public int MaxAttempts { get; set; } = 10;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay after the first failure; doubled after each further one (#31).</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest delay between two attempts.</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the dispatcher refreshes each outbox's status for the metric and the health check.</summary>
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Exponential back-off with jitter for failed deliveries (#31): base * 2^(failures - 1), capped, +/- 20 %. With the
/// defaults (5 s, 10 min, 10 attempts) a message is retried for about half an hour before it is parked.
/// </summary>
public static class OutboxBackoff
{
    public const double Jitter = 0.2;

    /// <param name="failures">Failed attempts so far, including the one that just happened (at least 1).</param>
    /// <param name="options">Base and cap of the back-off.</param>
    /// <param name="sample">A uniform random number in [0, 1), for the jitter.</param>
    public static TimeSpan Delay(int failures, OutboxOptions options, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        ArgumentNullException.ThrowIfNull(options);
        var exponential = options.BackoffBase.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 30));
        var capped = Math.Min(exponential, options.BackoffCap.TotalSeconds);
        return TimeSpan.FromSeconds(capped * (1 + (Jitter * ((2 * sample) - 1))));
    }
}

/// <summary>An outbox row claimed for delivery.</summary>
public sealed record OutboxEnvelope(Guid Id, string Type, string Payload);

/// <summary>Outcome of one batch: delivered ids and failures with their error.</summary>
public sealed record OutboxBatchResult(IReadOnlyList<Guid> Delivered, IReadOnlyList<(Guid Id, string Error)> Failed);

/// <summary>Delivery of one batch, independent of storage: every message is attempted, in order.</summary>
public static class OutboxProcessor
{
    public static async Task<OutboxBatchResult> ProcessAsync(
        IReadOnlyList<OutboxEnvelope> batch, Func<OutboxEnvelope, CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(publish);

        var delivered = new List<Guid>();
        var failed = new List<(Guid, string)>();
        foreach (var message in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await publish(message, cancellationToken).ConfigureAwait(false);
                delivered.Add(message.Id);
            }
#pragma warning disable CA1031 // A failing consumer must not block the rest of the batch; the error is stored.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                var error = ex.GetBaseException().Message;
                failed.Add((message.Id, error.Length > OutboxModelBuilderExtensions.MaxErrorLength
                    ? error[..OutboxModelBuilderExtensions.MaxErrorLength]
                    : error));
            }
        }

        return new OutboxBatchResult(delivered, failed);
    }
}

/// <summary>The outbox of one module, drained by <see cref="OutboxDispatcher"/>.</summary>
public interface IOutboxStore
{
    string Schema { get; }

    /// <summary>Claims, delivers and marks one batch in one transaction; returns the number of claimed messages.</summary>
    Task<int> DispatchBatchAsync(
        IServiceProvider scopedServices, Func<OutboxEnvelope, CancellationToken, Task> publish, OutboxOptions options, CancellationToken cancellationToken);

    /// <summary>Parked messages and the oldest message still waiting for delivery (#31).</summary>
    Task<OutboxStatus> GetStatusAsync(IServiceProvider scopedServices, CancellationToken cancellationToken);
}

/// <summary>Health of one module's outbox: parked messages and the oldest undelivered one.</summary>
public sealed record OutboxStatus(string Schema, long Exhausted, DateTimeOffset? OldestPending);

/// <summary>
/// EF/Npgsql outbox store. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several host
/// instances can dispatch concurrently without delivering a message twice at the same time.
/// </summary>
public sealed class EfOutboxStore<TContext>(string schema) : IOutboxStore
    where TContext : DbContext
{
    public string Schema { get; } = schema;

    public async Task<int> DispatchBatchAsync(
        IServiceProvider scopedServices, Func<OutboxEnvelope, CancellationToken, Task> publish, OutboxOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);
        ArgumentNullException.ThrowIfNull(options);
        var context = scopedServices.GetRequiredService<TContext>();
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var connection = context.Database.GetDbConnection();
            var dbTransaction = transaction.GetDbTransaction();

            var batch = new List<OutboxEnvelope>();
            var attempts = new Dictionary<Guid, int>();
            await using (var claim = Command(connection, dbTransaction, $"""
                SELECT id, type, payload::text, attempts FROM "{Schema}".outbox_message
                WHERE processed_at IS NULL AND exhausted_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= now())
                ORDER BY occurred_at
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
                """, ("batch", options.BatchSize)))
            await using (var reader = await claim.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    batch.Add(new OutboxEnvelope(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
                    attempts[reader.GetGuid(0)] = reader.GetInt32(3);
                }
            }

            if (batch.Count == 0)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return 0;
            }

            var result = await OutboxProcessor.ProcessAsync(batch, publish, ct).ConfigureAwait(false);

            if (result.Delivered.Count > 0)
            {
                await using var done = Command(connection, dbTransaction,
                    $"UPDATE \"{Schema}\".outbox_message SET processed_at = now(), error = NULL WHERE id = ANY(@ids)",
                    ("ids", result.Delivered.ToArray()));
                await done.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var (id, error) in result.Failed)
            {
                // Back off, or park the message once it has used up its attempts (#31).
                var failures = attempts[id] + 1;
                await using var fail = failures >= options.MaxAttempts
                    ? Command(connection, dbTransaction,
                        $"UPDATE \"{Schema}\".outbox_message SET attempts = @failures, error = @error, next_attempt_at = NULL, exhausted_at = now() WHERE id = @id",
                        ("id", id), ("error", error), ("failures", failures))
                    : Command(connection, dbTransaction,
                        $"UPDATE \"{Schema}\".outbox_message SET attempts = @failures, error = @error, next_attempt_at = now() + @delay WHERE id = @id",
                        ("id", id), ("error", error), ("failures", failures),
                        ("delay", OutboxBackoff.Delay(failures, options, Random.Shared.NextDouble())));
                await fail.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return batch.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxStatus> GetStatusAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);
        var context = scopedServices.GetRequiredService<TContext>();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The schema comes from the module's registration, never from input.
            command.CommandText = $"""
                SELECT (SELECT count(*) FROM "{Schema}".outbox_message WHERE exhausted_at IS NOT NULL AND processed_at IS NULL),
                       (SELECT min(occurred_at) FROM "{Schema}".outbox_message WHERE processed_at IS NULL AND exhausted_at IS NULL)
                """;
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxStatus(Schema, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // The schema comes from the module's registration, never from input; values are parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        return command;
    }
}

/// <summary>Delivers outbox messages to the integration event consumers of this process.</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// In-process transport of the single host (ADR 0003): every consumer of the event runs in its own module's unit of
/// work, and the inbox row is written in that transaction under the consumer's stable name (#32), so a redelivered
/// message is skipped. The service hosts use the RabbitMQ transport instead (ADR 0017); consumers don't change.
/// </summary>
public sealed class InProcessIntegrationEventPublisher(IServiceScopeFactory scopeFactory, UnitOfWorkRegistry registry) : IIntegrationEventPublisher
{
    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var integrationEvent = IntegrationEventSerializer.Deserialize(envelope.Type, envelope.Payload);
        var handlerType = typeof(IIntegrationEventConsumer<>).MakeGenericType(integrationEvent.GetType());
        var handle = handlerType.GetMethod(nameof(IIntegrationEventConsumer<IIntegrationEvent>.Handle))!;

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            foreach (var handler in scope.ServiceProvider.GetServices(handlerType).OfType<object>())
            {
                var consumer = IntegrationEventConsumers.NameOf(handler.GetType());
                var unitOfWork = registry.Resolve(handler.GetType().Assembly, scope.ServiceProvider);
                await unitOfWork.ExecuteInTransactionAsync(async ct =>
                {
                    if (!await unitOfWork.TryAcceptInboxMessageAsync(envelope.Id, consumer, ct).ConfigureAwait(false))
                    {
                        return false;                         // handled before: idempotent receiver
                    }

                    try
                    {
                        await ((Task)handle.Invoke(handler, [integrationEvent, ct])!).ConfigureAwait(false);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }

                    return true;
                }, _ => true, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
