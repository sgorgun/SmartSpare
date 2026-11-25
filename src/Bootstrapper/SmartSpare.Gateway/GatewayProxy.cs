using Microsoft.AspNetCore.Authentication;
using SmartSpare.ApiContracts;
using SmartSpare.Bff;
using SmartSpare.Infrastructure.Http;
using Yarp.ReverseProxy.Transforms;

namespace SmartSpare.Gateway;

/// <summary>The reverse proxy of the gateway (STAGE_4_PLAN 2.1, 2.2).</summary>
public static class GatewayProxy
{
    /// <summary>Request headers that belong to the browser session and never reach a service.</summary>
    public static IReadOnlyList<string> BrowserHeaders { get; } = ["Cookie", "Authorization", HttpHeaderNames.Antiforgery];

    public static IServiceCollection AddGatewayProxy(this IServiceCollection services)
    {
        services.AddSingleton<SessionTokenCache>();
        services.AddReverseProxy()
            .LoadFromMemory(GatewayRoutes.Routes(), GatewayRoutes.Clusters())
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(context =>
            {
                foreach (var header in BrowserHeaders)
                {
                    context.AddRequestHeaderRemove(header);
                }

                // The user's internal token for this session (ADR 0016 section 2). The route policy already refused
                // anonymous requests, so the user is authenticated here.
                context.AddRequestTransform(transform =>
                {
                    var http = transform.HttpContext;
                    string? sessionId = null;
                    http.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.Items
                        .TryGetValue(BffDefaults.SessionIdItem, out sessionId);
                    var token = string.IsNullOrEmpty(sessionId)
                        ? http.RequestServices.GetRequiredService<InternalTokenIssuer>().CreateToken(http.User, Guid.NewGuid().ToString("N"))
                        : http.RequestServices.GetRequiredService<SessionTokenCache>().GetToken(http.User, sessionId);
                    transform.ProxyRequest.Headers.Authorization = new("Bearer", token);
                    return ValueTask.CompletedTask;
                });
            });
        return services;
    }

    /// <summary>
    /// Maps the proxy with antiforgery in front (ADR 0016 section 5): an unsafe request without a valid token is
    /// answered 400 by the gateway and never forwarded.
    /// </summary>
    public static void MapGatewayProxy(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapReverseProxy(proxy =>
        {
            proxy.Use(async (context, next) =>
            {
                if (await AntiforgeryValidationFilter.CheckAsync(context).ConfigureAwait(false) is { } rejected)
                {
                    await rejected.ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        });
}
