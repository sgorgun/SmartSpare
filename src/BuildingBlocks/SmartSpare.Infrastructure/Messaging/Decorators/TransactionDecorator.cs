using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Decorators;

// Commands run inside a transaction of the owning module's DbContext (MODULE_MAPPING 5.6): the
// module is found by the command's assembly, the handler runs inside the execution strategy, and
// a failed Result or an exception rolls everything back. On success the pending changes and the
// integration events (as outbox rows) are written by one SaveChanges and committed together.
//
// An expected database error (a business rule raised by a trigger or function as SS100–SS402, a
// constraint or concurrency conflict) becomes a failed Result through DatabaseErrorMapper, after the
// rollback and after the execution strategy gave up retrying; anything else stays an exception.

internal sealed class TransactionCommandDecorator<TCommand>(
    ICommandHandler<TCommand> inner, UnitOfWorkRegistry registry, IServiceProvider services) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.Resolve(typeof(TCommand).Assembly, services)
                .ExecuteInTransactionAsync(ct => inner.Handle(command, ct), result => result.IsSuccess, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseErrorMapper.ToError(exception) is { } error)
        {
            return Result.Failure(error);
        }
    }
}

internal sealed class TransactionCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner, UnitOfWorkRegistry registry, IServiceProvider services) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.Resolve(typeof(TCommand).Assembly, services)
                .ExecuteInTransactionAsync(ct => inner.Handle(command, ct), result => result.IsSuccess, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseErrorMapper.ToError(exception) is { } error)
        {
            return Result.Failure<TResult>(error);
        }
    }
}
