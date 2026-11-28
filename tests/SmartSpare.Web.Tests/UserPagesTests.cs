using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>User administration and the own password change (#46): statuses, forms validated before the request, refusals.</summary>
public sealed class UserPagesTests : ClientTestContext
{
    private readonly FakeUsersClient _identity = new();

    public UserPagesTests()
    {
        Services.AddScoped<IdentityClient>(_ => _identity);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void Each_permission_of_a_user_shows_the_role_and_position_it_comes_from()
    {
        var page = Render<UsersPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=show-permissions]").Count.ShouldBe(2));

        page.FindAll("[data-testid=show-permissions]")[0].TextContent.ShouldContain("Permissions (2)");
        page.FindAll("[data-testid=show-permissions]")[0].Click();

        var lines = page.Find("[data-testid=user-permissions]").QuerySelectorAll("li").Select(li => li.TextContent).ToList();
        lines.ShouldBe(
        [
            $"{Permissions.Inventory.Stock.Adjust} ← Warehouse keeper ← position Warehouse keeper",
            $"{Permissions.Inventory.Stock.View} ← Warehouse keeper ← position Warehouse keeper",
        ]);
    }

    [Fact]
    public void The_directory_shows_each_user_with_position_rights_and_their_origin()
    {
        var page = Render<UsersPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.Markup.ShouldContain("egle.stankeviciute");
        page.FindAll("[data-testid=user-rights]")[0].TextContent.ShouldContain("Role Warehouse keeper: from position Warehouse keeper");
        page.FindAll("[data-testid=user-rights]")[0].TextContent.ShouldContain("Hub KLP: from Keeper of KLP");
        page.FindAll("[aria-label='Status: Locked out']").ShouldHaveSingleItem();
    }

    [Fact]
    public void Rights_are_not_edited_on_the_users_page()
    {
        var page = Render<UsersPage>();
        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));

        page.FindAll("[data-testid=new-user]").ShouldBeEmpty("accounts are created from employees (ADR 0026)");
        page.FindAll("[data-testid=edit-user]").ShouldBeEmpty("rights come from positions and assignments, never from the account");
        page.Find("[data-testid=rights-origin-note]").TextContent.ShouldContain("Roles come from the employee's position");
    }

    [Fact]
    public void Anonymisation_is_confirmed_first()
    {
        var page = Render<UsersPage>();
        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Anonymise").Click();
        _identity.Actions.ShouldBeEmpty();
        page.Find("[data-testid=user-confirm]").TextContent.ShouldContain("cannot be undone");
        page.Find("[data-testid=confirm-user-action]").Click();

        page.WaitForAssertion(() => _identity.Actions.ShouldHaveSingleItem().Action.ShouldBe("anonymise"));
    }

    [Fact]
    public void A_wrong_current_password_shows_the_servers_message()
    {
        _identity.NextPassword = ApiResult.Failure<NoContent>(new ProblemInfo(400, "Identity.WrongPassword", "Validation failed",
            "The current password is not correct.", "ref-pw"));
        var page = Render<ChangePasswordPage>();

        page.Find("input[data-testid=current-password], [data-testid=current-password] input").Change("old password");
        page.Find("input[data-testid=new-password], [data-testid=new-password] input").Change("a brand new passphrase");
        page.Find("[data-testid=change-password]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=password-error]").TextContent
            .ShouldBe("The current password is not correct. Reference: ref-pw"));
    }

    [Fact]
    public void A_short_new_password_is_refused_before_the_request()
    {
        var page = Render<ChangePasswordPage>();

        page.Find("input[data-testid=current-password], [data-testid=current-password] input").Change("old password");
        page.Find("input[data-testid=new-password], [data-testid=new-password] input").Change("short");
        page.Find("[data-testid=change-password]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=password-error]").TextContent.ShouldContain("at least 12 characters"));
        _identity.PasswordChanges.ShouldBe(0);
    }

}

/// <summary>An <see cref="IdentityClient"/> with two users.</summary>
public sealed class FakeUsersClient() : IdentityClient(new HttpClient())
{
    public List<(Guid Id, string Action)> Actions { get; } = [];

    public int PasswordChanges { get; private set; }

    public ApiResult<NoContent>? NextPassword { get; set; }

    private static readonly UserSummary Keeper = new(Guid.NewGuid(), "egle.stankeviciute", "E. S.", "e***@dealer.example", true, false, false, 3,
        [AppRoles.WarehouseKeeper], [], null, Guid.NewGuid(), "E0006", "Warehouse keeper",
        [new EffectiveRightDto("Role", AppRoles.WarehouseKeeper, "Position", "Warehouse keeper"), new EffectiveRightDto("Hub", "KLP", "WarehouseAssignment", "Keeper of KLP")],
        [], null);

    private static readonly UserSummary Locked = new(Guid.NewGuid(), "jonas.balciunas", "J. B.", "j***@dealer.example", true, true, false, null,
        [AppRoles.ServiceEngineer], [], null, Guid.NewGuid(), "E0005", "Service engineer",
        [new EffectiveRightDto("Role", AppRoles.ServiceEngineer, "Position", "Service engineer")], [], null);

    public override Task<ApiResult<PagedResponse<UserSummary>>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<UserSummary>([Keeper, Locked], page, pageSize, 2)));

    public override Task<ApiResult<List<RoleSummary>>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new List<RoleSummary>
        {
            new(4, AppRoles.WarehouseKeeper, "Warehouse keeper", null, [Permissions.Inventory.Stock.Adjust, Permissions.Inventory.Stock.View], false, false, ["WAREHOUSE_KEEPER"], true, false, null, "1"),
            new(5, AppRoles.ServiceEngineer, "Service engineer", null, [Permissions.Fleet.EngineHours.Record], false, false, ["SERVICE_ENGINEER"], true, false, null, "1"),
        }));

    public override Task<ApiResult<UserFormOptions>> GetUserFormOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new UserFormOptions(AppRoles.All, ["CLAAS", "JD"], [new HubOption(3, "KLP", "Klaipėda")])));

    public override Task<ApiResult<UserSummary>> UserActionAsync(Guid id, string action, CancellationToken cancellationToken = default)
    {
        Actions.Add((id, action));
        return Task.FromResult(ApiResult.Success(Keeper));
    }

    public override Task<ApiResult<NoContent>> ChangeOwnPasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        PasswordChanges++;
        return Task.FromResult(NextPassword ?? ApiResult.Success(new NoContent()));
    }
}
