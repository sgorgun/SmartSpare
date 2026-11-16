using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Decorators;

internal static partial class RequestLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Handling {RequestName}")]
    public static partial void Handling(ILogger logger, string requestName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Handled {RequestName} in {ElapsedMs} ms")]
    public static partial void Succeeded(ILogger logger, string requestName, long elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "{RequestName} failed with {ErrorCode} in {ElapsedMs} ms: {ErrorDescription}")]
    public static partial void Failed(ILogger logger, string requestName, string errorCode, long elapsedMs, string errorDescription);

    public static async Task<TResult> Measure<TResult>(ILogger logger, string requestName, Func<Task<TResult>> handle)
        where TResult : Result
    {
        Handling(logger, requestName);
        var started = Stopwatch.GetTimestamp();
        var result = await handle().ConfigureAwait(false);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.IsSuccess)
        {
            Succeeded(logger, requestName, elapsedMs);
        }
        else
        {
            Failed(logger, requestName, result.Error.Code, elapsedMs, result.Error.Description);
        }

        return result;
    }
}

internal sealed class LoggingCommandDecorator<TCommand>(
    ICommandHandler<TCommand> inner,
    ILogger<TCommand> logger) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public Task<Result> Handle(TCommand command, CancellationToken cancellationToken) =>
        RequestLog.Measure(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
}

internal sealed class LoggingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<TCommand> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken) =>
        RequestLog.Measure(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
}

internal sealed class LoggingQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<TQuery> logger) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken) =>
        RequestLog.Measure(logger, typeof(TQuery).Name, () => inner.Handle(query, cancellationToken));
}
