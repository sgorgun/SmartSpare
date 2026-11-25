using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scalar.AspNetCore;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Bff;

/// <summary>
/// The Scalar API reference at <c>/scalar</c> (ADR 0019, STAGE_4_PLAN section 7), served by the gateway and the single
/// host: one entry per module document, anonymous, no CDN fonts, no telemetry or AI features, and its inline script allowed
/// only by the nonce that
/// <see cref="SecurityHeaders.UseSecurityHeaders"/> puts into the CSP of the same response. Try-it calls are
/// same-origin and need a signed-in session and the antiforgery token, like any other client.
/// </summary>
public static class ApiReference
{
    public const string Path = "/scalar";

    /// <summary><c>HttpContext.Items</c> key of the per-request nonce.</summary>
    public const string NonceItem = "SmartSpare.ApiReference.Nonce";

    public static IEndpointConventionBuilder MapApiReference(this IEndpointRouteBuilder endpoints, IEnumerable<string> modules)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var documents = modules.ToList();
        return endpoints.MapScalarApiReference(Path, (options, http) =>
            {
                options.WithTitle("SmartSpare API")
                    .WithOpenApiRoutePattern(ApiDocumentation.RoutePattern)
                    .DisableDefaultFonts()
                    .AddDocuments(documents);

                // Nothing leaves for Scalar's own services: no usage telemetry, no AI agent or MCP server fed with the
                // documents, no hosted developer tools (deploy, SDK generation).
                options.DisableTelemetry()
                    .DisableAgent()
                    .DisableMcp()
                    .HideDeveloperTools();
                if (http.Items[NonceItem] is string nonce)
                {
                    options.WithNonce(nonce);
                }
            })
            .AllowAnonymous()
            .RequireRateLimiting(ApiDocumentation.Policy);
    }
}
