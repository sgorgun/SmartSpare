using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Server;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Bff;

/// <summary>
/// The authentication state the host serializes into the prerendered page for the WebAssembly client (STAGE_3_PLAN 1.4).
/// Only an allow-list of claims leaves the server: id, name, roles, brands and warehouse. The security stamp and anything
/// else (no personal data besides the user name) never reach the browser.
/// </summary>
public static class ClientAuthenticationState
{
    public static IReadOnlySet<string> AllowedClaimTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AppClaimTypes.Subject, AppClaimTypes.Name, AppClaimTypes.Role, AppClaimTypes.Brand, AppClaimTypes.Warehouse,
    };

    public static void Configure(AuthenticationStateSerializationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.SerializationCallback = state => ValueTask.FromResult(Serialize(state));
    }

    public static AuthenticationStateData? Serialize(AuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var data = new AuthenticationStateData { NameClaimType = AppClaimTypes.Name, RoleClaimType = AppClaimTypes.Role };
        foreach (var claim in state.User.Claims.Where(c => AllowedClaimTypes.Contains(c.Type)))
        {
            data.Claims.Add(new ClaimData(claim));
        }

        return data;
    }
}

/// <summary>
/// Security headers of every response (STAGE_3_PLAN 8.7, ADR 0014): a Content-Security-Policy header built from the
/// documented Blazor baseline, <c>X-Content-Type-Options</c> and <c>Referrer-Policy</c>. HSTS is added outside Development
/// by <c>UseHsts</c>.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// <c>script-src</c> stays strict: <c>'self'</c>, <c>'wasm-unsafe-eval'</c> for the .NET WebAssembly runtime and the hash of
    /// the inline import map; never <c>'unsafe-inline'</c> or <c>'unsafe-eval'</c>. <c>style-src</c> allows inline styles
    /// because MudBlazor computes style attributes at runtime (accepted risk, ADR 0014).
    /// </summary>
    public static string ContentSecurityPolicy(string? importMapHash) =>
        Policy(importMapHash is null ? "script-src 'self' 'wasm-unsafe-eval'" : $"script-src 'self' 'wasm-unsafe-eval' '{importMapHash}'");

    /// <summary>
    /// The policy of the API reference (<see cref="ApiReference.Path"/>, STAGE_4_PLAN section 7): its one inline module
    /// script runs by a per-request nonce, nothing else inline; no WebAssembly there. Everything else as on every page.
    /// </summary>
    public static string ApiReferencePolicy(string nonce) => Policy($"script-src 'self' 'nonce-{nonce}'");

    private static string Policy(string scriptSource) =>
        string.Join("; ",
            "default-src 'self'",
            scriptSource,
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'none'",
            "upgrade-insecure-requests");

    /// <summary>
    /// The SHA-256 source of the import map the <c>ImportMap</c> component renders for this endpoint. Fingerprinted assets
    /// change it per build, so it is computed per request, from what the component writes: the endpoint's
    /// <see cref="ImportMapDefinition"/> (<c>GetMetadata</c>, as the component reads it) as text (<c>ToString()</c> is the JSON
    /// the component renders). Line endings become LF, as in the ASP.NET Core 10 CSP guidance: the JSON uses the
    /// platform's newline, but HTML parsing turns CRLF into LF before the browser hashes an inline script.
    /// </summary>
    public static string? ImportMapHash(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var importMap = context.GetEndpoint()?.Metadata.GetMetadata<ImportMapDefinition>();
        if (importMap is null)
        {
            return null;
        }

        var text = importMap.ToString().ReplaceLineEndings("\n");
        return "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Adds the headers when the response starts, so the endpoint chosen by routing (and its import map) is known, while
    /// the middleware itself keeps its place in the pipeline order of STAGE_3_PLAN 8.1.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            // The API reference gets a fresh nonce; Scalar writes it into its inline script (ApiReference).
            if (context.Request.Path.StartsWithSegments(ApiReference.Path, StringComparison.OrdinalIgnoreCase))
            {
                // base64url: valid in a CSP nonce and never HTML-encoded in the attribute.
                context.Items[ApiReference.NonceItem] = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
            }

            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = context.Items[ApiReference.NonceItem] is string nonce
                    ? ApiReferencePolicy(nonce)
                    : ContentSecurityPolicy(ImportMapHash(context));
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                return Task.CompletedTask;
            });
            return next(context);
        });
}

/// <summary>
/// The host's fallback policy: an authenticated user, or the WebAssembly resource collection
/// (<c>_framework/resource-collection[.{fingerprint}].js[.gz]</c>). That file is served by component endpoints the framework
/// builds without endpoint conventions, so it can't be marked <c>AllowAnonymous</c> like the static assets; without the
/// exception an anonymous request gets the sign-in redirect, and WebAssembly can't start on the sign-in page. It lists
/// public asset URLs only. Every other endpoint without metadata still requires a signed-in user (AppPolicies).
/// </summary>
public sealed class AuthenticatedOrResourceCollectionRequirement : AuthorizationHandler<AuthenticatedOrResourceCollectionRequirement>, IAuthorizationRequirement
{
    private const string ResourceCollectionRoute = "_framework/resource-collection";

    public static void UseAsFallback(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.FallbackPolicy = new AuthorizationPolicyBuilder().AddRequirements(new AuthenticatedOrResourceCollectionRequirement()).Build();
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AuthenticatedOrResourceCollectionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.Identity?.IsAuthenticated == true || IsResourceCollection(context.Resource as HttpContext))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool IsResourceCollection(HttpContext? http) =>
        http?.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } pattern }
        && pattern.TrimStart('/').StartsWith(ResourceCollectionRoute, StringComparison.OrdinalIgnoreCase);
}
