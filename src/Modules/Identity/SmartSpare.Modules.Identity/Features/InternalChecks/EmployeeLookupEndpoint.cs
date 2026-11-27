using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Modules.Identity.Features.InternalChecks;

/// <summary>
/// POST /internal/v1/identity/employee-lookups — <see cref="IEmployeeDirectory"/> for other services, with the caller's
/// internal token (any signed-in caller), and POST /internal/v1/identity/account-owners for the names in a record's
/// history, and GET /internal/v1/identity/role-permissions for the gateway's permission map (ADR 0028). The gateway never
/// routes <c>/internal</c>.
/// </summary>
internal static class EmployeeLookupEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(IdentityInternalApi.RolePermissions, async (IRolePermissions roles, CancellationToken cancellationToken) =>
                Results.Ok(await roles.GetAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .ExcludeFromDescription();
        endpoints.MapPost(IdentityInternalApi.AccountOwners, async (Guid[] userIds, IEmployeeDirectory directory, CancellationToken cancellationToken) =>
                Results.Ok(await directory.FindAccountOwnersAsync(userIds, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .ExcludeFromDescription();
        endpoints.MapPost(IdentityInternalApi.EmployeeLookup, async (EmployeeLookupRequest request, IEmployeeDirectory directory, CancellationToken cancellationToken) =>
                Results.Ok(request.Ids.Count > 0
                    ? await directory.FindAsync(request.Ids, cancellationToken).ConfigureAwait(false)
                    : await directory.SearchAsync(request.Search, request.Limit, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .ExcludeFromDescription();
    }
}
