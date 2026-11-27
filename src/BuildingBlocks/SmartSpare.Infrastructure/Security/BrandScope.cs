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
/// Resource requirement of a brand-scoped action (ADR 0026 data scope, ADR 0028): the user holds the action's
/// <paramref name="Permission"/>, and either its <paramref name="AnyBrandPermission"/> (any document) or a brand claim for
/// every line of the document. A scope permission per action, not one for all of them, so a user whose roles combine a
/// brand-scoped approval with another role's unscoped right on a different action stays scoped, as before ADR 0028.
/// </summary>
public sealed record BrandScopedActionRequirement(string Permission, string AnyBrandPermission) : IAuthorizationRequirement;

/// <summary>The five brand-scoped actions of STAGE_3_PLAN section 3.3, keyed by their permission.</summary>
public static class BrandScopeRequirements
{
    public static BrandScopedActionRequirement ApproveTransferScope { get; } = new(Permissions.Inventory.Transfer.Approve, Permissions.Inventory.Transfer.ApproveAnyBrand);

    public static BrandScopedActionRequirement CancelTransferScope { get; } = new(Permissions.Inventory.Transfer.Cancel, Permissions.Inventory.Transfer.CancelAnyBrand);

    public static BrandScopedActionRequirement ApprovePurchaseOrderScope { get; } = new(Permissions.Procurement.PurchaseOrder.Approve, Permissions.Procurement.PurchaseOrder.ApproveAnyBrand);

    public static BrandScopedActionRequirement CancelPurchaseOrderScope { get; } = new(Permissions.Procurement.PurchaseOrder.Cancel, Permissions.Procurement.PurchaseOrder.CancelAnyBrand);

    public static BrandScopedActionRequirement DecideRecommendationScope { get; } = new(Permissions.Forecasting.Recommendation.Decide, Permissions.Forecasting.Recommendation.DecideAnyBrand);

    /// <summary>Each requirement by the permission it refines.</summary>
    public static IReadOnlyDictionary<string, BrandScopedActionRequirement> ByPermission { get; } = new Dictionary<string, BrandScopedActionRequirement>
    {
        [Permissions.Inventory.Transfer.Approve] = ApproveTransferScope,
        [Permissions.Inventory.Transfer.Cancel] = CancelTransferScope,
        [Permissions.Procurement.PurchaseOrder.Approve] = ApprovePurchaseOrderScope,
        [Permissions.Procurement.PurchaseOrder.Cancel] = CancelPurchaseOrderScope,
        [Permissions.Forecasting.Recommendation.Decide] = DecideRecommendationScope,
    };

    /// <summary>Registers the handler of <see cref="BrandScopedActionRequirement"/>.</summary>
    public static IServiceCollection AddBrandScopeAuthorization(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, BrandScopedActionHandler>());
        return services;
    }
}

/// <summary>
/// Evaluates <see cref="BrandScopedActionRequirement"/>: without the action's permission it fails; with
/// the action's any-brand permission it succeeds without looking at brands; otherwise only if the
/// document has lines and every line's brand is among the user's <see cref="AppClaimTypes.Brand"/> claims (403 else).
/// </summary>
internal sealed class BrandScopedActionHandler : AuthorizationHandler<BrandScopedActionRequirement, IBrandScoped>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, BrandScopedActionRequirement requirement, IBrandScoped resource)
    {
        var user = context.User;
        if (!user.HasClaim(AppClaimTypes.Permission, requirement.Permission))
        {
            return Task.CompletedTask;
        }

        if (user.HasClaim(AppClaimTypes.Permission, requirement.AnyBrandPermission))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (resource.BrandIds.Count > 0)
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
