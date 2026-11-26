using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// Forwards the internal token of the request being handled to a Contracts query of another service (ARCHITECTURE_DECISIONS
/// section 4: "an HTTP client to the owning service's <c>/internal/v1/...</c> endpoint, carrying the caller's internal
/// token"). The owning service then authorizes the same user. Without a bearer token on the current request nothing is
/// added, and the owning service answers 401: Contracts queries run only while handling a user request.
/// </summary>
public sealed class CallerTokenForwardingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (request.Headers.Authorization is null
            && AuthenticationHeaderValue.TryParse(authorization, out var header)
            && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(header.Parameter))
        {
            request.Headers.Authorization = header;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
