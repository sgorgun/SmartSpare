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
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_directory_shows_each_user_with_roles_hub_and_status()
    {
        var page = Render<UsersPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.Markup.ShouldContain("egle.stankeviciute");
        page.Markup.ShouldContain("Warehouse keeper");
        page.Markup.ShouldContain("KLP");
        page.FindAll("[aria-label='Status: Locked out']").ShouldHaveSingleItem();
    }

    [Fact]
    public void A_new_user_is_validated_by_the_shared_rules_before_the_request()
    {
        var page = Render<UsersPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=new-user]"));

        page.Find("[data-testid=new-user]").Click();
        page.Find("[data-testid=save-user]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=user-form-error]").TextContent.ShouldContain("Choose at least one role."));
        _identity.Created.ShouldBeEmpty();
    }

    [Fact]
    public void Changing_access_sends_the_roles_and_hub_of_the_form()
    {
        var page = Render<UsersPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=edit-user]").Count.ShouldBe(2));

        page.FindAll("[data-testid=edit-user]")[0].Click();
        page.Find("[data-testid=save-user]").Click();

        page.WaitForAssertion(() => _identity.AccessChanges.ShouldHaveSingleItem().Access
            .ShouldBe(new UserAccessRequest([AppRoles.WarehouseKeeper], [], 3), new AccessComparer()));
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

    private sealed class AccessComparer : IEqualityComparer<UserAccessRequest>
    {
        public bool Equals(UserAccessRequest? x, UserAccessRequest? y) =>
            x is not null && y is not null && x.Roles.SequenceEqual(y.Roles) && x.Brands.SequenceEqual(y.Brands) && x.HomeWarehouseId == y.HomeWarehouseId;

        public int GetHashCode(UserAccessRequest obj) => 0;
    }
}

/// <summary>An <see cref="IdentityClient"/> with two users.</summary>
public sealed class FakeUsersClient() : IdentityClient(new HttpClient())
{
    public List<CreateUserRequest> Created { get; } = [];

    public List<(Guid Id, UserAccessRequest Access)> AccessChanges { get; } = [];

    public List<(Guid Id, string Action)> Actions { get; } = [];

    public int PasswordChanges { get; private set; }

    public ApiResult<NoContent>? NextPassword { get; set; }

    private static readonly UserSummary Keeper = new(Guid.NewGuid(), "egle.stankeviciute", "E. S.", "e***@dealer.example", true, false, false, 3,
        [AppRoles.WarehouseKeeper], [], null);

    private static readonly UserSummary Locked = new(Guid.NewGuid(), "jonas.balciunas", "J. B.", "j***@dealer.example", true, true, false, null,
        [AppRoles.ServiceEngineer], [], null);

    public override Task<ApiResult<PagedResponse<UserSummary>>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<UserSummary>([Keeper, Locked], page, pageSize, 2)));

    public override Task<ApiResult<UserFormOptions>> GetUserFormOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new UserFormOptions(AppRoles.All, ["CLAAS", "JD"], [new HubOption(3, "KLP", "Klaipėda")])));

    public override Task<ApiResult<NoContent>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        return Task.FromResult(ApiResult.Success(new NoContent()));
    }

    public override Task<ApiResult<UserSummary>> UpdateUserAccessAsync(Guid id, UserAccessRequest request, CancellationToken cancellationToken = default)
    {
        AccessChanges.Add((id, request));
        return Task.FromResult(ApiResult.Success(Keeper));
    }

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
