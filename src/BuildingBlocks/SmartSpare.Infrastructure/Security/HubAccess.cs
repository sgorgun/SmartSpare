using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Security;

/// <summary>
/// Write access to a hub (ADR 0026 decision 5): ADMINISTRATOR is unrestricted, everyone else needs the hub among their
/// hubs (keeper or service-base assignments, the <c>ss:warehouse</c> claims). The same rule as <c>util.require_hub</c>,
/// which guards the SQL writes (SS302); reads are never scoped by hub.
/// </summary>
public static class HubAccess
{
    public static bool MayWrite(ICurrentUser user, short warehouseId) => Of(user).Allows(warehouseId);

    /// <summary>The user's write access to hubs, for the domain (which knows no claims).</summary>
    public static HubRights Of(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new HubRights(user.Roles.Contains(AppRoles.Administrator), user.WarehouseIds.ToHashSet());
    }
}
