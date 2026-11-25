using SmartSpare.Infrastructure.Http;
using Yarp.ReverseProxy.Configuration;

namespace SmartSpare.Gateway;

/// <summary>
/// The gateway's routes, built in code (STAGE_4_PLAN 2.1, product owner 25 Nov 2026). Two routes per module, both with
/// the literal module name, never a route parameter:
/// <list type="bullet">
/// <item><c>api-{module}</c>: <c>/api/v1/{module}/{**rest}</c>, the versioned business API (ADR 0012).</item>
/// <item><c>ops-{module}</c>: <c>/api/{module}/{**rest}</c>, the unversioned operational endpoints (ping, diagnostics).
/// With a literal name, <c>/api/v1/...</c> can't match it; an architecture rule keeps business endpoints under v1.</item>
/// </list>
/// Plus <c>openapi-{module}</c> for the module's document. Nothing else is routed: <c>/internal/*</c> and the services'
/// <c>/health/*</c> never reach a service.
/// </summary>
public static class GatewayRoutes
{
    /// <summary>
    /// The modules behind the gateway, in catalogue order. The gateway references no module, so the names are listed
    /// here; a test keeps them equal to the ModuleCatalog.
    /// </summary>
    public static IReadOnlyList<string> Modules { get; } = ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    /// <summary>The default authorization policy: an authenticated user, decided before anything is proxied.</summary>
    public const string Policy = "default";

    public static IReadOnlyList<RouteConfig> Routes() =>
    [
        .. Modules.SelectMany(module => new[]
        {
            new RouteConfig
            {
                RouteId = $"api-{module}",
                ClusterId = module,
                AuthorizationPolicy = Policy,
                Match = new RouteMatch { Path = $"/api/v1/{module}/{{**rest}}" },
            },
            new RouteConfig
            {
                RouteId = $"ops-{module}",
                ClusterId = module,
                AuthorizationPolicy = Policy,
                Match = new RouteMatch { Path = $"/api/{module}/{{**rest}}" },
            },
            new RouteConfig
            {
                RouteId = $"{DocumentRoutePrefix}{module}",
                ClusterId = module,
                AuthorizationPolicy = "anonymous",
                OutputCachePolicy = ApiDocumentation.Policy,
                RateLimiterPolicy = ApiDocumentation.Policy,
                Match = new RouteMatch { Path = ApiDocumentation.PathOf(module), Methods = ["GET"] },
            },
        }),
    ];

    /// <summary>
    /// Route id prefix of the module documents (ADR 0019): the service's own <c>/openapi/{module}.json</c>, anonymous,
    /// GET only, cached for 10 minutes. No token is added to these requests.
    /// </summary>
    public const string DocumentRoutePrefix = "openapi-";

    /// <summary>
    /// One cluster per module with the destination <c>http://{module}</c>: Container Apps' internal DNS by app name in
    /// Azure, Aspire service discovery locally.
    /// </summary>
    public static IReadOnlyList<ClusterConfig> Clusters() =>
    [
        .. Modules.Select(module => new ClusterConfig
        {
            ClusterId = module,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                [module] = new() { Address = $"http://{module}" },
            },
        }),
    ];
}
