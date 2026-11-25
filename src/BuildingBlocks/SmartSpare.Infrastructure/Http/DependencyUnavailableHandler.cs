using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SmartSpare.ApiContracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// Answers <see cref="DependencyUnavailableException"/> with 503 and <see cref="ProblemCodes.ServiceUnavailable"/>
/// (STAGE_4_PLAN section 4): the browser sees "try again" with the trace id, never a business error. Other exceptions
/// fall through to the default 500.
/// </summary>
internal sealed partial class DependencyUnavailableHandler(IProblemDetailsService problemDetails, ILogger<DependencyUnavailableHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not DependencyUnavailableException unavailable)
        {
            return false;
        }

        LogUnavailable(unavailable.Service ?? "unknown", unavailable);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service unavailable",
                Detail = "A service needed for this request did not answer. Please try again.",
                Extensions = { [ProblemDetailsRegistration.CodeKey] = ProblemCodes.ServiceUnavailable },
            },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Service} service did not answer.")]
    private partial void LogUnavailable(string service, Exception exception);
}
