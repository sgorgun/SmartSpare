using System.Globalization;
using System.Security.Claims;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;

namespace SmartSpare.Infrastructure;

/// <summary>
/// Permission authorization (ADR 0028) with the real authorization service: the provider, the default role bundles and
/// the proof that they reproduce exactly the role policies they replaced (PR 7x1), and the shared sign-in validator.
/// </summary>
public sealed class AppPoliciesTests
{
    /// <summary>
    /// The role policies before PR 7x1, frozen: each policy, its roles and the permissions that replace it. This is the
    /// mapping table of STAGE_7_PLAN 1.8; the default bundles must give every role exactly the permissions of its old
    /// policies, plus the any-brand variant where a role was unscoped for a brand-scoped action.
    /// </summary>
    public static readonly IReadOnlyList<(string Policy, string[] Roles, string[] Permissions)> LegacyPolicies =
    [
        ("Administration", [AppRoles.Administrator],
            [Permissions.Organisation.Employee.View, Permissions.Organisation.Employee.Create, Permissions.Organisation.Employee.Edit,
             Permissions.Organisation.Employee.Archive, Permissions.Organisation.Employee.Anonymise, Permissions.Organisation.Position.View,
             Permissions.Organisation.Position.Create, Permissions.Organisation.Position.Edit, Permissions.Organisation.Position.Archive,
             Permissions.Organisation.Account.View, Permissions.Organisation.Account.Create, Permissions.Organisation.Account.Edit,
             Permissions.Organisation.Role.View, Permissions.Organisation.Role.Edit,
             Permissions.Inventory.Brand.ManageAssignments, Permissions.Inventory.Warehouse.ManageAssignments]),
        ("PlanInventory", [AppRoles.BrandManager, AppRoles.InventoryPlanner],
            [Permissions.Inventory.Stock.EditLevels, Permissions.Inventory.Demand.Flag, Permissions.Inventory.Transfer.Create, Permissions.Procurement.PurchaseOrder.Create]),
        ("ApproveTransfer", [AppRoles.BrandManager, AppRoles.InventoryPlanner], [Permissions.Inventory.Transfer.Approve]),
        ("CancelTransfer", [AppRoles.BrandManager, AppRoles.InventoryPlanner, AppRoles.Administrator], [Permissions.Inventory.Transfer.Cancel]),
        ("MoveStock", [AppRoles.WarehouseKeeper, AppRoles.Administrator],
            [Permissions.Inventory.Stock.Adjust, Permissions.Inventory.Transfer.Dispatch, Permissions.Inventory.Transfer.Receive,
             Permissions.Procurement.PurchaseOrder.Receive]),
        ("RecordServiceWork", [AppRoles.ServiceEngineer, AppRoles.Administrator],
            [Permissions.Fleet.EngineHours.Record, Permissions.Fleet.ServiceOrder.Start, Permissions.Fleet.ServiceOrder.Complete]),
        ("ManageServiceOrders", [AppRoles.ServiceEngineer, AppRoles.BrandManager, AppRoles.Administrator],
            [Permissions.Fleet.ServiceOrder.Create, Permissions.Fleet.ServiceOrder.Edit, Permissions.Fleet.ServiceOrder.Cancel]),
        ("ApprovePurchaseOrder", [AppRoles.BrandManager, AppRoles.Sponsor], [Permissions.Procurement.PurchaseOrder.Approve]),
        ("CancelPurchaseOrder", [AppRoles.BrandManager, AppRoles.Sponsor, AppRoles.Administrator], [Permissions.Procurement.PurchaseOrder.Cancel]),
        ("DecideRecommendation", [AppRoles.BrandManager, AppRoles.InventoryPlanner], [Permissions.Forecasting.Recommendation.Decide]),
        ("ReadAudit", [AppRoles.Sponsor, AppRoles.Administrator], [Permissions.Audit.Log.View]),
        ("ImportData", [AppRoles.Administrator, AppRoles.InventoryPlanner], [Permissions.Inventory.Import.View, Permissions.Inventory.Import.Run]),
        ("ManageCatalog", [AppRoles.InventoryPlanner, AppRoles.Administrator],
            [Permissions.Inventory.AdjustmentReason.Create, Permissions.Inventory.AdjustmentReason.Edit, Permissions.Inventory.AdjustmentReason.Archive]),
        ("ReadInventory", [.. AppRoles.All],
            [Permissions.Inventory.Part.View, Permissions.Inventory.Brand.View, Permissions.Inventory.Warehouse.View, Permissions.Inventory.Stock.View,
             Permissions.Inventory.Transfer.View, Permissions.Inventory.AdjustmentReason.View]),
        ("ReadFleet", [.. AppRoles.All], [Permissions.Fleet.Machine.View, Permissions.Fleet.ServiceOrder.View]),

        // Data scope before PR 7x1, written with role names: ADMINISTRATOR was unrestricted at every hub (HubAccess,
        // util.require_hub), and these roles were unscoped for each brand-scoped action (BrandScopeRequirements).
        ("unrestricted hubs", [AppRoles.Administrator], [Permissions.Organisation.Scope.AllHubs]),
        ("unscoped: approve transfer", [AppRoles.InventoryPlanner], [Permissions.Inventory.Transfer.ApproveAnyBrand]),
        ("unscoped: cancel transfer", [AppRoles.InventoryPlanner, AppRoles.Administrator], [Permissions.Inventory.Transfer.CancelAnyBrand]),
        ("unscoped: approve purchase order", [AppRoles.Sponsor], [Permissions.Procurement.PurchaseOrder.ApproveAnyBrand]),
        ("unscoped: cancel purchase order", [AppRoles.Sponsor, AppRoles.Administrator], [Permissions.Procurement.PurchaseOrder.CancelAnyBrand]),
        ("unscoped: decide recommendation", [AppRoles.InventoryPlanner], [Permissions.Forecasting.Recommendation.DecideAnyBrand]),

        // Added after PR 7x1, each with its default holders (ADR 0028).
        ("7x3: supplier maintenance", [AppRoles.Administrator], [Permissions.Procurement.Supplier.Edit]),
        ("7h2: brand catalogue", [AppRoles.Administrator, AppRoles.InventoryPlanner], [Permissions.Inventory.Brand.Create, Permissions.Inventory.Brand.Edit, Permissions.Inventory.Brand.Archive]),
        ("7h3: read manufacturers", [.. AppRoles.All], [Permissions.Inventory.Manufacturer.View]),
        ("7h3: manufacturer catalogue", [AppRoles.Administrator, AppRoles.InventoryPlanner], [Permissions.Inventory.Manufacturer.Create, Permissions.Inventory.Manufacturer.Edit, Permissions.Inventory.Manufacturer.Archive]),
        ("7i1: parts catalogue", [AppRoles.Administrator, AppRoles.InventoryPlanner], [Permissions.Inventory.Part.Create, Permissions.Inventory.Part.Edit, Permissions.Inventory.Part.Archive]),
    ];

    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddPermissionAuthorization()
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    public static TheoryData<string> Roles => [.. AppRoles.All];

    public static TheoryData<string> PermissionNames => [.. Permissions.All.Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Roles))]
    public void A_default_role_has_exactly_the_permissions_of_its_old_policies(string role) =>
        DefaultRoles.Bundles[role].Order(StringComparer.Ordinal).ShouldBe(
            LegacyPolicies.Where(p => p.Roles.Contains(role)).SelectMany(p => p.Permissions).Distinct().Order(StringComparer.Ordinal));

    [Fact]
    public void Every_permission_has_recorded_default_holders() =>
        Permissions.All.Select(p => p.Name).Except(LegacyPolicies.SelectMany(p => p.Permissions)).ShouldBeEmpty(
            "every permission is either an old policy's or recorded here with the PR that added it");

    [Fact]
    public void No_default_role_breaks_separation_of_duties() =>
        AppRoles.All.ShouldAllBe(r => DutyConflicts.FindIn(DefaultRoles.Bundles[r]) == null);

    [Fact]
    public void The_catalogue_names_are_unique_and_follow_module_resource_action()
    {
        Permissions.All.Select(p => p.Name).ShouldBeUnique();
        Permissions.All.ShouldAllBe(p => System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"^[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+\.[A-Z][A-Za-z]+$")
                                         && p.Name == $"{p.Module}.{p.Resource}.{p.Action}" && p.Description.Length > 0);
    }

    [Theory]
    [MemberData(nameof(PermissionNames))]
    public async Task A_permission_policy_allows_exactly_the_holders_of_the_permission(string permission)
    {
        foreach (var role in AppRoles.All)
        {
            var result = await Authorization.AuthorizeAsync(User([role]), permission);
            result.Succeeded.ShouldBe(DefaultRoles.Bundles[role].Contains(permission), $"{role} / {permission}");
        }
    }

    [Fact]
    public async Task A_role_claim_alone_passes_no_permission_policy()
    {
        var roleOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AppClaimTypes.Role, AppRoles.Administrator)], "test"));

        (await Authorization.AuthorizeAsync(roleOnly, Permissions.Organisation.Employee.View)).Succeeded.ShouldBeFalse(
            "endpoints check permissions, never role names");
    }

    [Fact]
    public async Task An_unknown_policy_name_fails_loudly() =>
        await Should.ThrowAsync<InvalidOperationException>(() => Authorization.AuthorizeAsync(User([AppRoles.Administrator]), "Administration"));

    [Fact]
    public async Task The_fallback_policy_requires_an_authenticated_user()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var options = new AuthorizationOptions();
        AppPolicies.Register(options);

        (await Authorization.AuthorizeAsync(anonymous, options.FallbackPolicy!)).Succeeded.ShouldBeFalse();
        (await Authorization.AuthorizeAsync(User([]), options.FallbackPolicy!)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Anonymous_users_pass_no_permission()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AppClaimTypes.Permission, Permissions.Organisation.Employee.View)]));

        (await Authorization.AuthorizeAsync(anonymous, Permissions.Organisation.Employee.View)).Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tomas.radzevicius", "any password", true)]
    [InlineData("", "any password", false)]
    [InlineData("tomas.radzevicius", "", false)]
    [InlineData("a-name-that-is-longer-than-the-fifty-characters-allowed", "x", false)]
    public void The_sign_in_validator_checks_presence_and_length_only(string userName, string password, bool valid)
    {
        var result = new LoginRequestValidator().TestValidate(new LoginRequest(userName, password));

        result.IsValid.ShouldBe(valid);
    }

    [Fact]
    public void The_sign_in_validator_caps_the_password_length()
    {
        new LoginRequestValidator().TestValidate(new LoginRequest("tomas", new string('x', LoginRequestValidator.MaxPasswordLength + 1)))
            .ShouldHaveValidationErrorFor(r => r.Password);
    }

    private static ClaimsPrincipal User(string[] roles, params short[] brands) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(AppClaimTypes.Role, r))
                .Concat(DefaultRoles.Of(roles).Select(p => new Claim(AppClaimTypes.Permission, p)))   // what the BFF adds
                .Concat(brands.Select(b => new Claim(AppClaimTypes.Brand, b.ToString(CultureInfo.InvariantCulture))))
                .Append(new Claim(AppClaimTypes.Subject, Guid.NewGuid().ToString())),
            "test",
            AppClaimTypes.Name,
            AppClaimTypes.Role));
}
