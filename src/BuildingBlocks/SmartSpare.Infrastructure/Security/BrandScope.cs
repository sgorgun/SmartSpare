using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Infrastructure.Security;

/// <summary>A document whose approval is brand-scoped: the distinct brands of all its lines (STAGE_3_PLAN section 3.3).</summary>
public interface IBrandScoped
{
    IReadOnlySet<short> BrandIds { get; }
}

/// <summary>
/// Resource requirement of an approval or decision action. A user with any of <paramref name="UnscopedRoles"/> may act on
/// any document; a user with <paramref name="ScopedRole"/> only on documents whose every line has one of their brands.
/// The unscoped roles plus the scoped role equal the roles of the action's policy (parity rule, section 3.2).
/// </summary>
public sealed record BrandScopedActionRequirement(IReadOnlySet<string> UnscopedRoles, string ScopedRole) : IAuthorizationRequirement;

/// <summary>The five brand-scoped actions of STAGE_3_PLAN section 3.3, keyed by the policy each one refines.</summary>
public static class BrandScopeRequirements
{
    public static BrandScopedActionRequirement ApproveTransferScope { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { AppRoles.InventoryPlanner }, AppRoles.BrandManager);

    public static BrandScopedActionRequirement CancelTransferScope { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { AppRoles.InventoryPlanner, AppRoles.Administrator }, AppRoles.BrandManager);

    public static BrandScopedActionRequirement ApprovePurchaseOrderScope { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { AppRoles.Sponsor }, AppRoles.BrandManager);

    public static BrandScopedActionRequirement CancelPurchaseOrderScope { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { AppRoles.Sponsor, AppRoles.Administrator }, AppRoles.BrandManager);

    public static BrandScopedActionRequirement DecideRecommendationScope { get; } =
        new(new HashSet<string>(StringComparer.Ordinal) { AppRoles.InventoryPlanner }, AppRoles.BrandManager);

    /// <summary>Each requirement with the policy (<see cref="AppPolicies"/>) whose role gate it refines.</summary>
    public static IReadOnlyDictionary<string, BrandScopedActionRequirement> ByPolicy { get; } = new Dictionary<string, BrandScopedActionRequirement>
    {
        [AppPolicies.ApproveTransfer] = ApproveTransferScope,
        [AppPolicies.CancelTransfer] = CancelTransferScope,
        [AppPolicies.ApprovePurchaseOrder] = ApprovePurchaseOrderScope,
        [AppPolicies.CancelPurchaseOrder] = CancelPurchaseOrderScope,
        [AppPolicies.DecideRecommendation] = DecideRecommendationScope,
    };

    /// <summary>Registers the handler of <see cref="BrandScopedActionRequirement"/>.</summary>
    public static IServiceCollection AddBrandScopeAuthorization(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, BrandScopedActionHandler>());
        return services;
    }
}

/// <summary>
/// Evaluates <see cref="BrandScopedActionRequirement"/> in a fixed order: an unscoped role succeeds without looking at
/// brands; otherwise the scoped role succeeds only if the document has lines and every line's brand is among the user's
/// <see cref="AppClaimTypes.Brand"/> claims; anything else is not satisfied (403). Deny by default.
/// </summary>
internal sealed class BrandScopedActionHandler : AuthorizationHandler<BrandScopedActionRequirement, IBrandScoped>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, BrandScopedActionRequirement requirement, IBrandScoped resource)
    {
        var user = context.User;

        if (requirement.UnscopedRoles.Any(user.IsInRole))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (user.IsInRole(requirement.ScopedRole) && resource.BrandIds.Count > 0)
        {
            var ownBrands = user.FindAll(AppClaimTypes.Brand)
                .Select(c => short.TryParse(c.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (short?)null)
                .OfType<short>()
                .ToHashSet();

            if (resource.BrandIds.All(ownBrands.Contains))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
