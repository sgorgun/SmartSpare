using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// ProblemDetails for every error response (RFC 9457), with the W3C trace id as correlation id (#60): business
/// errors, validation, 401/403, antiforgery, 429 and unhandled exceptions all carry <c>traceId</c>, so a user can quote
/// it and the request can be found in the traces and logs.
/// </summary>
public static class ProblemDetailsRegistration
{
    public const string TraceIdKey = "traceId";
    public const string CodeKey = "code";

    public static IServiceCollection AddSmartSpareProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions[TraceIdKey] = TraceId(context.HttpContext);

            // Responses without a body (challenge, forbid, rate limiter) are written by the status-code pages;
            // give them the stable code of their kind.
            if (!context.ProblemDetails.Extensions.ContainsKey(CodeKey) && CodeFor(context.ProblemDetails.Status) is { } code)
            {
                context.ProblemDetails.Extensions[CodeKey] = code;
            }
        });

    /// <summary>
    /// Writes ProblemDetails for unhandled exceptions and for error responses without a body (401, 403, 404, 429).
    /// First in the pipeline, so it covers everything after it.
    /// </summary>
    public static IApplicationBuilder UseSmartSpareProblemDetails(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>The W3C trace id of the request (<c>Activity.Current.TraceId</c>), or the request id without a trace.</summary>
    public static string TraceId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return Activity.Current is { } activity ? activity.TraceId.ToHexString() : httpContext.TraceIdentifier;
    }

    private static string? CodeFor(int? status) => status switch
    {
        StatusCodes.Status401Unauthorized => ProblemCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ProblemCodes.Forbidden,
        StatusCodes.Status429TooManyRequests => ProblemCodes.TooManyRequests,
        _ => null,
    };
}
