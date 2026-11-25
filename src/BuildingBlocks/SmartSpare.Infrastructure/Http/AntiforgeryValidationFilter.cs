using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// Validates the antiforgery token on every unsafe request (POST, PUT, PATCH, DELETE) of the <c>/bff</c> and
/// <c>/api</c> endpoints (STAGE_3_PLAN section 1.5, ADR 0014). .NET 10's <c>UseAntiforgery</c> only checks endpoints
/// with antiforgery metadata (form binding), not JSON endpoints, so this filter does it. A failure is a 400
/// ProblemDetails with <see cref="ProblemCodes.AntiforgeryFailed"/>.
/// <para>
/// Requests authenticated with an internal token skip the check (ADR 0016 section 5): they come from the gateway or
/// a service, never from a browser, and carry no cookie. Every other request is checked, including anonymous ones
/// such as sign-in, which protects against login CSRF.
/// </para>
/// </summary>
public sealed class AntiforgeryValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return await CheckAsync(context.HttpContext).ConfigureAwait(false) ?? await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The check itself, also used by the gateway in front of its proxy: <see langword="null"/> when the request may
    /// continue, otherwise the 400 to answer.
    /// </summary>
    public static async Task<IResult?> CheckAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!IsUnsafe(http.Request.Method) || IsInternalTokenRequest(http))
        {
            return null;
        }

        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Antiforgery validation failed",
                detail: "The request is missing a valid antiforgery token. Reload the page and try again.",
                extensions: new Dictionary<string, object?> { [ProblemDetailsRegistration.CodeKey] = ProblemCodes.AntiforgeryFailed });
        }
    }

    private static bool IsUnsafe(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static bool IsInternalTokenRequest(HttpContext http) =>
        http.User.Identity is { IsAuthenticated: true, AuthenticationType: InternalTokenDefaults.AuthenticationType };
}
