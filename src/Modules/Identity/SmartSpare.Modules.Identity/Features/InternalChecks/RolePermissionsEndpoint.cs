using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Modules.Identity.Features.InternalChecks;

/// <summary>
/// GET /internal/v1/identity/role-permissions for the gateway's permission map (ADR 0028), one of the gateway's
/// synchronous calls to Identity listed in ADR 0029 amendment 2. The gateway never routes <c>/internal</c>.
/// </summary>
internal static class RolePermissionsEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(IdentityInternalApi.RolePermissions, async (IRolePermissions roles, CancellationToken cancellationToken) =>
                Results.Ok(await roles.GetAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .ExcludeFromDescription();
}
