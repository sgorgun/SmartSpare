using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Bff;

/// <summary>
/// Adds the user's permissions to every authenticated request of a BFF host (ADR 0028): the session carries only the role
/// codes, and the permissions are the union of those roles' bundles, read through <see cref="IRolePermissions"/> and kept
/// by <see cref="RolePermissionCache"/> for the session check interval, the same window in which any rights change reaches
/// a session. Permissions therefore never sit in the cookie; they reach the internal token and the client's
/// authentication state from the transformed principal. A principal that already carries permissions (an internal token
/// the gateway issued) and a service's own token are left alone.
/// </summary>
internal sealed class PermissionClaimsTransformation(IRolePermissions roles, RolePermissionCache cache) : IClaimsTransformation
{
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

        var map = await cache.GetAsync(() => roles.GetAsync(CancellationToken.None)).ConfigureAwait(false);
        var permissions = roleCodes
            .SelectMany(r => map.TryGetValue(r, out var bundle) ? bundle : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(p => new Claim(AppClaimTypes.Permission, p));
        var transformed = principal.Clone();
        transformed.AddIdentity(new ClaimsIdentity(permissions));
        return transformed;
    }
}

/// <summary>
/// The role → permissions map of a BFF host, reloaded when it is older than the session check interval. Time comes from
/// the registered <see cref="TimeProvider"/>, so tests move a fake clock instead of waiting (CLAUDE.md, stage 6 lesson).
/// </summary>
internal sealed class RolePermissionCache(TimeProvider clock, IOptions<SessionCheckOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _map;
    private DateTimeOffset _loadedAt;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(Func<Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (Fresh() is { } map)
        {
            return map;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Fresh() is { } loaded)
            {
                return loaded;
            }

            _map = await load().ConfigureAwait(false);
            _loadedAt = clock.GetUtcNow();
            return _map;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private IReadOnlyDictionary<string, IReadOnlyList<string>>? Fresh() =>
        _map is not null && clock.GetUtcNow() - _loadedAt < options.Value.CheckInterval ? _map : null;
}
