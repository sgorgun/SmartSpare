using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Infrastructure.Security;

/// <summary>Authentication and authorization of a service host (ADR 0016 section 4): internal tokens only.</summary>
public static class ServiceAuthentication
{
    /// <summary>
    /// JwtBearer for internal tokens as the only scheme: no cookie, no antiforgery, so a service accepts nothing a browser
    /// could send. Every endpoint needs an authenticated caller (fallback policy); module policies are the same as in the
    /// single host (<see cref="AppPolicies"/>, brand scope), plus <see cref="InternalTokenDefaults.GatewayOnlyPolicy"/>.
    /// </summary>
    public static IServiceCollection AddServiceAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthentication(InternalTokenDefaults.Scheme).AddInternalToken();
        services.AddAuthorization(options =>
        {
            AppPolicies.Register(options);
            AddGatewayOnlyPolicy(options);
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        services.AddBrandScopeAuthorization();
        return services;
    }

    /// <summary>The caller is the gateway acting as itself: an internal token with <c>ss:svc=gateway</c>.</summary>
    public static void AddGatewayOnlyPolicy(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(InternalTokenDefaults.GatewayOnlyPolicy, policy => policy
            .AddAuthenticationSchemes(InternalTokenDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(InternalTokenDefaults.ServiceClaim, InternalTokenDefaults.GatewayService));
    }
}
