using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// <see cref="ICurrentUser"/> from the claims of the current request (STAGE_3_PLAN section 6, #11). The cookie principal and
/// the internal-token principal carry the same claim types (<see cref="AppClaimTypes"/>, ADR 0016 section 4), so the
/// session context is the same whichever scheme authenticated the request. Outside a request, or for an anonymous one,
/// there is no user.
/// </summary>
public sealed class ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? User =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public Guid? UserId => Guid.TryParse(User?.FindFirstValue(AppClaimTypes.Subject), out var id) ? id : null;

    public string? Reason => null;

    public IReadOnlyCollection<string> Roles =>
        User is { } user ? [.. user.FindAll(AppClaimTypes.Role).Select(c => c.Value)] : [];

    public IReadOnlyCollection<short> WarehouseIds =>
        User is { } user
            ? [.. user.FindAll(AppClaimTypes.Warehouse)
                .Select(c => short.TryParse(c.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (short?)null)
                .OfType<short>().Distinct().Order()]
            : [];
}

/// <summary>Registration of the request-based session context in a host.</summary>
public static class CurrentUserRegistration
{
    /// <summary>
    /// Uses the claims of the request as the session context of every module transaction (replaces "no user", which
    /// the modules register for processes without requests).
    /// </summary>
    public static IServiceCollection AddClaimsCurrentUser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser, ClaimsCurrentUser>());
        return services;
    }
}
