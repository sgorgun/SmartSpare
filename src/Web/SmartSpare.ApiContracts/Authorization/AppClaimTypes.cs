namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// Claim types of a SmartSpare principal (STAGE_3_PLAN section 3.1). The cookie principal and the internal-token
/// principal carry exactly these types (ADR 0016 section 4), so policies and the current user behave the same in
/// both. Short JWT names are used in both, without the framework's long URI mapping.
/// </summary>
public static class AppClaimTypes
{
    /// <summary>User id (<c>iam.app_user.id</c>).</summary>
    public const string Subject = "sub";

    /// <summary>User name; the name claim type of every SmartSpare identity.</summary>
    public const string Name = "name";

    /// <summary>Role code, 0..n; the role claim type of every SmartSpare identity.</summary>
    public const string Role = "role";

    /// <summary>Brand id the user is responsible for (<c>iam.user_brand</c>), 0..n.</summary>
    public const string Brand = "ss:brand";

    /// <summary>Home warehouse id (<c>iam.app_user.home_warehouse_id</c>), 0..1.</summary>
    public const string Warehouse = "ss:warehouse";

    /// <summary>
    /// A permission of the user (ADR 0028), 0..n. Added server-side from the user's roles on every request (never stored
    /// in the cookie); carried in the internal token and the client's authentication state.
    /// </summary>
    public const string Permission = "ss:perm";

    /// <summary>The permissions in the internal token, as a <see cref="PermissionBitset"/> (ADR 0028); expanded on validation.</summary>
    public const string PermissionSet = "ss:perms";

    /// <summary>The <see cref="Permissions.CatalogueVersion"/> the issuer encoded <see cref="PermissionSet"/> with.</summary>
    public const string PermissionVersion = "ss:permv";

    /// <summary>Cookie session id, carried into internal tokens (ADR 0016 section 2).</summary>
    public const string SessionId = "sid";
}
