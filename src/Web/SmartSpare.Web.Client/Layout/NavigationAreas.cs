using Microsoft.AspNetCore.Authorization;
using MudBlazor;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Web.Client.Layout;

/// <summary>An area of the app in the menu and on the home page, shown to users who pass any of its policies.</summary>
public sealed record NavigationArea(string Href, string TitleKey, string DescriptionKey, string Icon, int Stage, IReadOnlyList<string> Policies);

/// <summary>
/// The menu, by policy (STAGE_3_PLAN 8.2). An area with a read policy (Inventory: <see cref="AppPolicies.ReadInventory"/>)
/// is shown to everyone who may read it. Until the read policies of stages 6–9 exist, the other areas are shown to the
/// users who may act in them, using the action policies of section 3.2; an empty list means every signed-in user. This
/// is UX only: every endpoint checks its own policy on the server.
/// </summary>
public static class NavigationAreas
{
    public static IReadOnlyList<NavigationArea> All { get; } =
    [
        new("inventory", "Nav.Inventory", "Module.Inventory", Icons.Material.Filled.Inventory2, 5,
            [AppPolicies.ReadInventory]),
        new("fleet", "Nav.Fleet", "Module.Fleet", Icons.Material.Filled.Agriculture, 6,
            [AppPolicies.RecordServiceWork, AppPolicies.ManageServiceOrders]),
        new("procurement", "Nav.Procurement", "Module.Procurement", Icons.Material.Filled.ShoppingCart, 7,
            [AppPolicies.PlanInventory, AppPolicies.ApprovePurchaseOrder, AppPolicies.CancelPurchaseOrder, AppPolicies.MoveStock]),
        new("forecasting", "Nav.Forecasting", "Module.Forecasting", Icons.Material.Filled.Insights, 8,
            [AppPolicies.DecideRecommendation]),
        new("reporting", "Nav.Reporting", "Module.Reporting", Icons.Material.Filled.BarChart, 9, []),
    ];
}

/// <summary>Which areas a user sees.</summary>
public static class NavigationAccess
{
    public static async Task<IReadOnlyList<NavigationArea>> VisibleAsync(
        Microsoft.AspNetCore.Authorization.IAuthorizationService authorization, System.Security.Claims.ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return [];
        }

        var visible = new List<NavigationArea>();
        foreach (var area in NavigationAreas.All)
        {
            if (area.Policies.Count == 0)
            {
                visible.Add(area);
                continue;
            }

            foreach (var policy in area.Policies)
            {
                if ((await authorization.AuthorizeAsync(user, policy).ConfigureAwait(false)).Succeeded)
                {
                    visible.Add(area);
                    break;
                }
            }
        }

        return visible;
    }
}
