using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>What a command handler sees: collect integration events, save the module's changes.</summary>
public interface IUnitOfWork
{
    /// <summary>Queues an event; it becomes an outbox row in the same SaveChanges as the data (MODULE_MAPPING 5.6).</summary>
    void AddIntegrationEvent(IIntegrationEvent integrationEvent);

    /// <summary>Saves pending changes and queued events together.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The unit of work of one module context; inject it as <c>IUnitOfWork&lt;XDbContext&gt;</c> inside the module.</summary>
#pragma warning disable CA1711 // Generic parameter in the name is intentional: one unit of work per module context.
public interface IUnitOfWork<TContext> : IUnitOfWork
#pragma warning restore CA1711
    where TContext : DbContext;

/// <summary>The side the pipeline uses: the transaction around a command and the inbox of a consumer.</summary>
public interface ITransactionalUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction of the module context, inside the execution
    /// strategy. Commits (after saving pending changes and events) when <paramref name="commit"/>
    /// returns true; otherwise, and on any exception, rolls back and forgets every pending change.
    /// A call while a transaction is already open joins it.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work, Func<TResult, bool> commit, CancellationToken cancellationToken);

    /// <summary>Records <paramref name="messageId"/> for <paramref name="consumer"/>; false if it was handled before.</summary>
    Task<bool> TryAcceptInboxMessageAsync(Guid messageId, string consumer, CancellationToken cancellationToken);
}

/// <summary>EF Core unit of work over one module DbContext.</summary>
public sealed class EfUnitOfWork<TContext>(TContext context) : IUnitOfWork<TContext>, ITransactionalUnitOfWork
    where TContext : DbContext
{
    private readonly List<IIntegrationEvent> _events = [];

    public void AddIntegrationEvent(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            throw new InvalidOperationException($"{typeof(TContext).Name} has no outbox: call AddOutboxAndInbox in OnModelCreating.");
        }

        _events.Add(integrationEvent);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (_events.Count > 0)
        {
            context.Set<OutboxMessage>().AddRange(_events.Select(IntegrationEventSerializer.ToOutboxMessage));
            _events.Clear();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work, Func<TResult, bool> commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(commit);

        if (context.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);   // nested command: the outer one commits
        }

        // EnableRetryOnFailure forbids user transactions outside the execution strategy; a retry
        // starts from a clean change tracker and event list.
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            Reset();
            await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            TResult result;
            try
            {
                result = await work(ct).ConfigureAwait(false);
                if (commit(result))
                {
                    await SaveChangesAsync(ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return result;
                }
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                Reset();
                throw;
            }

            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            Reset();
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAcceptInboxMessageAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        var table = context.Model.FindEntityType(typeof(InboxMessage))
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} has no inbox: call AddOutboxAndInbox in OnModelCreating.");
        var schema = table.GetSchema() ?? throw new InvalidOperationException("The inbox table has no schema.");

        // The schema comes from the model, never from input; the values are parameters.
#pragma warning disable EF1002, S2077 // Identifier interpolation is intentional (schema from the EF model).
        var inserted = await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO \"{schema}\".inbox_message (message_id, consumer) VALUES (@id, @consumer) ON CONFLICT DO NOTHING",
            [new NpgsqlParameter("id", messageId), new NpgsqlParameter("consumer", consumer)],
            cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002, S2077
        return inserted == 1;
    }

    private void Reset()
    {
        _events.Clear();
        context.ChangeTracker.Clear();
    }
}

/// <summary>Which unit of work runs the commands of a module assembly (registered by each module).</summary>
public sealed record UnitOfWorkRegistration(Assembly ModuleAssembly, Type UnitOfWorkType);

/// <summary>Resolves the module of a command by its assembly (MODULE_MAPPING 5.6).</summary>
public sealed class UnitOfWorkRegistry
{
    private readonly Dictionary<Assembly, Type> _byAssembly;

    public UnitOfWorkRegistry(IEnumerable<UnitOfWorkRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var all = registrations.ToList();
        if (all.GroupBy(r => r.ModuleAssembly).FirstOrDefault(g => g.Skip(1).Any()) is { } duplicate)
        {
            throw new InvalidOperationException($"{duplicate.Key.GetName().Name} registers more than one unit of work.");
        }

        _byAssembly = all.ToDictionary(r => r.ModuleAssembly, r => r.UnitOfWorkType);
    }

    public bool IsRegistered(Assembly moduleAssembly) => _byAssembly.ContainsKey(moduleAssembly);

    public ITransactionalUnitOfWork Resolve(Assembly moduleAssembly, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(moduleAssembly);
        ArgumentNullException.ThrowIfNull(services);
        return _byAssembly.TryGetValue(moduleAssembly, out var type)
            ? (ITransactionalUnitOfWork)services.GetRequiredService(type)
            : throw new InvalidOperationException($"No unit of work is registered for {moduleAssembly.GetName().Name}.");
    }
}
