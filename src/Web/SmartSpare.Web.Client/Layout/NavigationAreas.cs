using Microsoft.AspNetCore.Authorization;
using MudBlazor;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Web.Client.Layout;

/// <summary>An area of the app in the menu and on the home page, shown to users who hold any of its permissions.</summary>
public sealed record NavigationArea(string Href, string TitleKey, string DescriptionKey, string Icon, int Stage, IReadOnlyList<string> Policies);

/// <summary>
/// The menu, by permission (ADR 0028): an area is shown to users who hold any of its permissions; an empty list means
/// every signed-in user. This is UX only: every endpoint checks its own permission on the server.
/// </summary>
public static class NavigationAreas
{
    public static IReadOnlyList<NavigationArea> All { get; } =
    [
        new("inventory", "Nav.Inventory", "Module.Inventory", Icons.Material.Filled.Inventory2, 5,
            [Permissions.Inventory.Part.View]),
        new("fleet", "Nav.Fleet", "Module.Fleet", Icons.Material.Filled.Agriculture, 6,
            [Permissions.Fleet.Machine.View]),
        new("procurement", "Nav.Procurement", "Module.Procurement", Icons.Material.Filled.ShoppingCart, 7,
            [Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.PurchaseOrder.Approve, Permissions.Procurement.PurchaseOrder.Cancel, Permissions.Procurement.PurchaseOrder.Receive]),
        new("forecasting", "Nav.Forecasting", "Module.Forecasting", Icons.Material.Filled.Insights, 8,
            [Permissions.Forecasting.Recommendation.Decide]),
        new("reporting", "Nav.Reporting", "Module.Reporting", Icons.Material.Filled.BarChart, 9, []),
        new("admin/employees", "Nav.Organisation", "Module.Organisation", Icons.Material.Filled.ManageAccounts, 7, [Permissions.Organisation.Employee.View]),
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
