using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging;

// Own CQRS abstractions instead of MediatR (ADR 0005). Handlers are resolved directly from DI
// by their interface; cross-cutting behaviour is added with decorators (see HandlerRegistration).

#pragma warning disable CA1040 // Marker interfaces are intentional: they tie a request to its handler type.

/// <summary>A state-changing request without a return value.</summary>
public interface ICommand;

/// <summary>A state-changing request that returns <typeparamref name="TResult"/>.</summary>
public interface ICommand<TResult>;

/// <summary>A read-only request that returns <typeparamref name="TResult"/>.</summary>
public interface IQuery<TResult>;

#pragma warning restore CA1040

public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}
