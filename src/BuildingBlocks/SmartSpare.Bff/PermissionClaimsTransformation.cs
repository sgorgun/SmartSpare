using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Bff;

/// <summary>
/// Adds the user's permissions to every authenticated request of a BFF host (ADR 0028): the session carries only the role
/// codes, and the permissions are the union of those roles' bundles, read through <see cref="IRolePermissions"/> and
/// cached for <see cref="BffDefaults.SessionCheckInterval"/>, the same window in which a role change reaches a session.
/// Permissions therefore never sit in the cookie; they reach the internal token and the client's authentication state
/// from the transformed principal. A principal that already carries permissions (an internal token the gateway
/// issued) and a service's own token are left alone.
/// </summary>
internal sealed class PermissionClaimsTransformation(IRolePermissions roles, IMemoryCache cache) : IClaimsTransformation
{
    private const string CacheKey = "smartspare:role-permissions";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true
            || principal.HasClaim(c => c.Type is AppClaimTypes.Permission or InternalTokenDefaults.ServiceClaim))
        {
            return principal;
        }

        var roleCodes = principal.FindAll(AppClaimTypes.Role).Select(c => c.Value).ToList();
        if (roleCodes.Count == 0)
        {
            return principal;
        }

        var map = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BffDefaults.SessionCheckInterval;
            return await roles.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var permissions = roleCodes
            .SelectMany(r => map!.TryGetValue(r, out var bundle) ? bundle : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(p => new Claim(AppClaimTypes.Permission, p));
        var transformed = principal.Clone();
        transformed.AddIdentity(new ClaimsIdentity(permissions));
        return transformed;
    }
}
