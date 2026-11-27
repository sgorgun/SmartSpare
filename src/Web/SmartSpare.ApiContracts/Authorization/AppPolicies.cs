using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// Authorization of SmartSpare (ADR 0028): every policy is a permission of <see cref="Permissions"/>, built on demand
/// by <see cref="PermissionPolicyProvider"/>; a fallback policy requires an authenticated user, so an endpoint without
/// metadata is never public by accident (anonymous endpoints opt out with <c>AllowAnonymous</c>). Policies never name
/// a role or an authentication scheme. Brand and hub scope are resource checks on top (ADR 0026) and never widen it.
/// </summary>
public static class AppPolicies
{
    /// <summary>The fallback policy (an authenticated user). Permission policies come from the provider.</summary>
    public static void Register(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }

    /// <summary>Authorization with the permission policies, for the hosts, the services and the WebAssembly client.</summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorizationCore(Register);
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        return services;
    }
}

/// <summary>
/// Turns a permission name into the policy "authenticated, with this <see cref="AppClaimTypes.Permission"/> claim"
/// (ADR 0028), the ASP.NET Core permission-based pattern; any other name falls back to the registered policies. An
/// unknown name has no policy, which the authorization service refuses with an exception rather than allowing.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (Permissions.Exists(policyName))
        {
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(AppClaimTypes.Permission, policyName).Build();
        }

        // Other names (registered policies, and names such as YARP's "default") go to the default provider; an unknown
        // name returns null, and the authorization service then refuses it with an exception, never by allowing.
        return await base.GetPolicyAsync(policyName).ConfigureAwait(false);
    }
}
