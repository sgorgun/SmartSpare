namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>GET /bff/me</c>, built from the claims of the session without a database call.</summary>
public sealed record CurrentUserResponse(
    bool IsAuthenticated,
    Guid? UserId,
    string? UserName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<short> BrandIds,
    short? HomeWarehouseId)
{
    public static CurrentUserResponse Anonymous { get; } = new(false, null, null, [], [], null);
}
