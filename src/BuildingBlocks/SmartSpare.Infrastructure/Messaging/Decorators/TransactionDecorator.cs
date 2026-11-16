using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Decorators;

// Commands run inside a database transaction of the owning module's DbContext, so that the data
// change and its outbox messages commit together (ADR 0003). There are no DbContexts until stage 2,
// so for now these decorators only mark the place in the pipeline and pass the call through.

internal sealed class TransactionCommandDecorator<TCommand>(ICommandHandler<TCommand> inner) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public Task<Result> Handle(TCommand command, CancellationToken cancellationToken) =>
        inner.Handle(command, cancellationToken);
}

internal sealed class TransactionCommandDecorator<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken) =>
        inner.Handle(command, cancellationToken);
}
