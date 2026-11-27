using System.ComponentModel;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>GET /bff/me</c>, built from the claims of the session without a database call.</summary>
[Description("The user of the current session, or an anonymous one.")]
public sealed record CurrentUserResponse(
    [property: Description("False for an anonymous caller; the other members are then empty.")] bool IsAuthenticated,
    [property: Description("User id.")] Guid? UserId,
    [property: Description("User name.")] string? UserName,
    [property: Description("Role codes, for example BRAND_MANAGER.")] IReadOnlyList<string> Roles,
    [property: Description("Ids of the brands the user is responsible for (brand scope of approvals).")] IReadOnlyList<short> BrandIds,
    [property: Description("Hubs the user may write at (keeper or service-base assignments, ADR 0026); empty for none.")] IReadOnlyList<short> HomeWarehouseIds)
{
    public static CurrentUserResponse Anonymous { get; } = new(false, null, null, [], [], []);
}
