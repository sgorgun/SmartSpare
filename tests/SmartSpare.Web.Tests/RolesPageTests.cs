using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The roles × permissions screen (ADR 0028, PR 7x4): grouped by module, searchable, locked roles read-only.</summary>
public sealed class RolesPageTests : ClientTestContext
{
    private readonly FakeRolesClient _identity = new();

    public RolesPageTests()
    {
        Services.AddScoped<IdentityClient>(_ => _identity);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_matrix_groups_permissions_by_module_with_one_column_per_role()
    {
        var page = Roles(AppRoles.Administrator);

        page.WaitForAssertion(() => page.FindAll("[data-testid=role-column]").Count.ShouldBe(2));
        page.FindAll("[data-testid=module-row]").Select(r => r.TextContent.Trim()).ShouldBe(["Inventory", "Procurement"]);
        page.FindAll("[data-testid=permission-row]").Count.ShouldBe(3);
        page.FindAll("[data-testid=role-column]")[0].TextContent.ShouldContain("locked");
    }

    [Fact]
    public void The_search_narrows_the_permissions()
    {
        var page = Roles(AppRoles.Administrator);
        page.WaitForAssertion(() => page.FindAll("[data-testid=permission-row]").Count.ShouldBe(3));

        page.Find("input[data-testid=permission-search], [data-testid=permission-search] input").Input("approve");

        page.WaitForAssertion(() => page.FindAll("[data-testid=permission-row]").ShouldHaveSingleItem().TextContent.ShouldContain(Permissions.Procurement.PurchaseOrder.Approve));
    }

    [Fact]
    public void A_change_of_an_editable_role_is_saved_with_its_version_and_locked_roles_stay_read_only()
    {
        var page = Roles(AppRoles.Administrator);
        page.WaitForAssertion(() => page.FindAll("[data-testid=permission-row]").Count.ShouldBe(3));

        // Two roles per row: the locked Administrator column first, then the clerk.
        var stockRow = page.FindAll("[data-testid=permission-row]")[1].QuerySelectorAll("input[type=checkbox]");
        stockRow[0].HasAttribute("disabled").ShouldBeTrue("the locked role is read-only");
        stockRow[1].Change(true);
        page.Find("[data-testid=save-role]").Click();

        page.WaitForAssertion(() => _identity.Updates.ShouldHaveSingleItem());
        var (id, request, version) = _identity.Updates[0];
        (id, version).ShouldBe(((short)8, "41"));
        request.Permissions.ShouldBe([Permissions.Inventory.Part.View, Permissions.Inventory.Stock.View], ignoreOrder: true);
    }

    [Fact]
    public void A_refused_change_explains_why()
    {
        _identity.NextUpdate = ApiResult.Failure<RoleSummary>(new ProblemInfo(409, "Identity.DutyConflict", null,
            "Separation of duties: Procurement.PurchaseOrder.Create and Procurement.Supplier.Edit cannot be held together (ADR 0028).", "t9"));
        var page = Roles(AppRoles.Administrator);
        page.WaitForAssertion(() => page.FindAll("[data-testid=permission-row]").Count.ShouldBe(3));

        page.FindAll("[data-testid=permission-row]")[2].QuerySelectorAll("input[type=checkbox]")[1].Change(true);
        page.Find("[data-testid=save-role]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=roles-error]").TextContent.ShouldContain("Separation of duties"));
    }

    [Fact]
    public void Without_role_editing_the_matrix_is_read_only()
    {
        var page = Roles(AppRoles.Sponsor);

        page.WaitForAssertion(() => page.FindAll("[data-testid=permission-row]").Count.ShouldBe(3));
        page.FindAll("[data-testid=new-role-form]").ShouldBeEmpty();
        page.FindAll("input[type=checkbox]").ShouldAllBe(c => c.HasAttribute("disabled"));
    }

    private IRenderedComponent<RolesPage> Roles(string role)
    {
        // A sponsor cannot open the page in the app (Organisation.Role.View); the test renders it to check the read-only state.
        var user = User("tester", role);
        if (role == AppRoles.Sponsor)
        {
            ((System.Security.Claims.ClaimsIdentity)user.Identity!).AddClaim(new System.Security.Claims.Claim(AppClaimTypes.Permission, Permissions.Organisation.Role.View));
        }

        return Render<RolesPage>(p => p.AddCascadingValue(State(user)));
    }
}

/// <summary>An <see cref="IdentityClient"/> with two roles and three permissions.</summary>
public sealed class FakeRolesClient() : IdentityClient(new HttpClient())
{
    public List<(short Id, UpdateRoleRequest Request, string Version)> Updates { get; } = [];

    public ApiResult<RoleSummary>? NextUpdate { get; set; }

    public override Task<ApiResult<List<PermissionDto>>> ListPermissionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new List<PermissionDto>
        {
            new(Permissions.Inventory.Part.View, "Inventory", "See the parts catalogue."),
            new(Permissions.Inventory.Stock.View, "Inventory", "See stock."),
            new(Permissions.Procurement.PurchaseOrder.Approve, "Procurement", "Approve purchase orders."),
        }));

    public override Task<ApiResult<List<RoleSummary>>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new List<RoleSummary>
        {
            new(1, AppRoles.Administrator, "Administrator", null, [Permissions.Inventory.Part.View], true, false, ["SYSTEM_ADMINISTRATOR"], "7"),
            new(8, "CLERK", "Clerk", null, [Permissions.Inventory.Part.View], false, false, [], "41"),
        }));

    public override Task<ApiResult<RoleSummary>> UpdateRoleAsync(short id, UpdateRoleRequest request, string version, CancellationToken cancellationToken = default)
    {
        Updates.Add((id, request, version));
        return Task.FromResult(NextUpdate ?? ApiResult.Success(new RoleSummary(id, "CLERK", request.Name, null, request.Permissions, false, false, [], "42")));
    }
}
