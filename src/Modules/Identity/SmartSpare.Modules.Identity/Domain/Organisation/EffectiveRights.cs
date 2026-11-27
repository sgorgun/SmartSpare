namespace SmartSpare.Modules.Identity.Domain.Organisation;

/// <summary>What kind of right: an application role, a brand of the approval scope, or a hub of the write access.</summary>
internal enum RightKind
{
    Role,
    Brand,
    Hub,
}

/// <summary>Where a right comes from; every right has exactly one origin, shown on the admin screen.</summary>
internal enum RightOrigin
{
    /// <summary>The role template of the employee's position.</summary>
    Position,

    /// <summary>A brand-manager assignment on a brand (STAGE_7_PLAN 1.2); until PR 7d the user's brand list.</summary>
    BrandAssignment,

    /// <summary>A keeper or service-base assignment on a warehouse; until PR 7d the user's home hub.</summary>
    WarehouseAssignment,
}

/// <summary>A brand or hub fact about an employee: the record's id and code, and the assignment it comes from.</summary>
internal sealed record ScopeFact(short Id, string Code, string OriginName);

/// <summary>Everything the derivation needs about one account (STAGE_7_PLAN section 1.2).</summary>
internal sealed record RightsInput(
    bool AccountActive,
    bool EmployeeActive,
    string PositionCode,
    string PositionName,
    IReadOnlyCollection<string> PositionRoles,
    IReadOnlyCollection<ScopeFact> Brands,
    IReadOnlyCollection<ScopeFact> Hubs);

/// <summary>One effective right with its origin, e.g. role BRAND_MANAGER from position "Brand manager".</summary>
internal sealed record EffectiveRight(RightKind Kind, string Value, short? Id, RightOrigin Origin, string OriginName);

/// <summary>The effective rights of an account, and the facts that have no effect (shown, never applied).</summary>
internal sealed record EffectiveRights(IReadOnlyList<EffectiveRight> Rights, IReadOnlyList<EffectiveRight> WithoutEffect, string? InactiveReason)
{
    public IEnumerable<string> Roles => Rights.Where(r => r.Kind == RightKind.Role).Select(r => r.Value);

    public IEnumerable<short> BrandIds => Rights.Where(r => r.Kind == RightKind.Brand).Select(r => r.Id!.Value);

    public IEnumerable<short> HubIds => Rights.Where(r => r.Kind == RightKind.Hub).Select(r => r.Id!.Value);
}

/// <summary>
/// Derives an account's rights from the organisation (ADR 0026, STAGE_7_PLAN section 1.2). Pure: the same input always
/// gives the same rights, so the claims, the admin screen and the tests agree by construction.
/// <list type="bullet">
/// <item>Roles: exactly the role template of the employee's position.</item>
/// <item>Brand scope: a brand-manager assignment, effective only while the position grants BRAND_MANAGER.</item>
/// <item>Hub access: keeper or service-base assignments on warehouses.</item>
/// <item>Nothing at all for a locked account or an employee who left or was anonymised.</item>
/// </list>
/// </summary>
internal static class EffectiveRightsCalculator
{
    public const string BrandManagerRole = "BRAND_MANAGER";

    public static EffectiveRights Calculate(RightsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rights = new List<EffectiveRight>();
        var withoutEffect = new List<EffectiveRight>();

        rights.AddRange(input.PositionRoles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(role => new EffectiveRight(RightKind.Role, role, null, RightOrigin.Position, input.PositionName)));

        var brandManager = rights.Exists(r => r.Value == BrandManagerRole);
        foreach (var brand in input.Brands.DistinctBy(b => b.Id).OrderBy(b => b.Code, StringComparer.Ordinal))
        {
            (brandManager ? rights : withoutEffect).Add(
                new EffectiveRight(RightKind.Brand, brand.Code, brand.Id, RightOrigin.BrandAssignment, brand.OriginName));
        }

        rights.AddRange(input.Hubs.DistinctBy(h => h.Id).OrderBy(h => h.Code, StringComparer.Ordinal)
            .Select(hub => new EffectiveRight(RightKind.Hub, hub.Code, hub.Id, RightOrigin.WarehouseAssignment, hub.OriginName)));

        string? inactive = null;
        if (!input.EmployeeActive)
        {
            inactive = "The employee has left or was anonymised.";
        }
        else if (!input.AccountActive)
        {
            inactive = "The account is locked by an administrator.";
        }

        return inactive is null
            ? new EffectiveRights(rights, withoutEffect, null)
            : new EffectiveRights([], [.. rights, .. withoutEffect], inactive);
    }
}
