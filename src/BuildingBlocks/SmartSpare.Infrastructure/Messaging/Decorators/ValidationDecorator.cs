using FluentValidation;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Decorators;

/// <summary>Runs all FluentValidation validators of a request and short-circuits with a <see cref="ValidationError"/>.</summary>
internal static class RequestValidator
{
    public static async Task<ValidationError?> ValidateAsync<TRequest>(
        IEnumerable<IValidator<TRequest>> validators, TRequest request, CancellationToken cancellationToken)
    {
        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var errors = failures
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return new ValidationError(errors);
    }
}

internal sealed class ValidationCommandDecorator<TCommand>(
    ICommandHandler<TCommand> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, command, cancellationToken).ConfigureAwait(false);
        return error is null
            ? await inner.Handle(command, cancellationToken).ConfigureAwait(false)
            : Result.Failure(error);
    }
}

internal sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, command, cancellationToken).ConfigureAwait(false);
        return error is null
            ? await inner.Handle(command, cancellationToken).ConfigureAwait(false)
            : Result.Failure<TResult>(error);
    }
}

internal sealed class ValidationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, query, cancellationToken).ConfigureAwait(false);
        return error is null
            ? await inner.Handle(query, cancellationToken).ConfigureAwait(false)
            : Result.Failure<TResult>(error);
    }
}
