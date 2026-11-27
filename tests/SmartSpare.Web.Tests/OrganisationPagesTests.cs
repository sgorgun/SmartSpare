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

/// <summary>
/// Positions and employees (ADR 0026): the role template is edited only on a position, accounts are created only from
/// employees, forms use the shared validators before the request, and writes carry the version read (If-Match).
/// </summary>
public sealed class OrganisationPagesTests : ClientTestContext
{
    private readonly FakeOrganisationClient _identity = new();

    public OrganisationPagesTests()
    {
        Services.AddScoped<IdentityClient>(_ => _identity);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void Positions_show_their_role_template_and_active_employees()
    {
        var page = Render<PositionsPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.FindAll("[data-testid=position-roles]")[0].TextContent.ShouldBe("Brand manager, Sponsor");
        page.FindAll("[data-testid=position-roles]")[1].TextContent.ShouldBe("No roles");
    }

    [Fact]
    public void Saving_a_template_sends_the_roles_with_the_version_read()
    {
        var page = Render<PositionsPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=edit-position]").Count.ShouldBe(2));

        page.FindAll("[data-testid=edit-position]")[0].Click();
        page.Find("[data-testid=save-position]").Click();

        page.WaitForAssertion(() => _identity.PositionUpdates.ShouldHaveSingleItem());
        var (id, request, version) = _identity.PositionUpdates[0];
        (id, version).ShouldBe(((short)3, "41"));
        request.Roles.ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor]);
    }

    [Fact]
    public void A_refused_template_change_shows_the_servers_sentence()
    {
        _identity.NextPositionUpdate = ApiResult.Failure<PositionSummary>(new ProblemInfo(409, "Identity.LastAdministrator", "Conflict",
            "This change would leave SmartSpare without an active administrator.", "ref-adm"));
        var page = Render<PositionsPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=edit-position]").Count.ShouldBe(2));

        page.FindAll("[data-testid=edit-position]")[0].Click();
        page.Find("[data-testid=save-position]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=position-form-error]").TextContent
            .ShouldBe("This change would leave SmartSpare without an active administrator. Reference: ref-adm"));
    }

    [Fact]
    public void A_new_position_is_validated_before_the_request()
    {
        var page = Render<PositionsPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=new-position]"));

        page.Find("[data-testid=new-position]").Click();
        page.Find("[data-testid=save-position]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=position-form-error]").TextContent.ShouldContain("2 to 30 letters"));
        _identity.PositionsCreated.ShouldBe(0);
    }

    [Fact]
    public void An_account_is_created_from_an_employee_with_the_password_rule_checked_first()
    {
        var page = Render<EmployeesPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=create-account]").ShouldHaveSingleItem());

        page.Find("[data-testid=create-account]").Click();
        page.Find("[data-testid=account-form]").TextContent.ShouldContain("roles of the position Accountant");
        page.Find("input[data-testid=account-user-name], [data-testid=account-user-name] input").Change("ona.z");
        page.Find("input[data-testid=account-password], [data-testid=account-password] input").Change("short");
        page.Find("[data-testid=save-account]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=account-form-error]").TextContent.ShouldContain("at least 12 characters"));
        _identity.Accounts.ShouldBeEmpty();

        page.Find("input[data-testid=account-password], [data-testid=account-password] input").Change("a long initial password");
        page.Find("[data-testid=save-account]").Click();
        page.WaitForAssertion(() => _identity.Accounts.ShouldHaveSingleItem().UserName.ShouldBe("ona.z"));
    }

    [Fact]
    public void Leaving_sends_the_date_and_the_version_read()
    {
        var page = Render<EmployeesPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=leave-employee]").Count.ShouldBe(2));

        page.FindAll("[data-testid=leave-employee]")[0].Click();
        page.Find("[data-testid=leave-form]").TextContent.ShouldContain("within 30 seconds");
        page.Find("input[data-testid=left-on], [data-testid=left-on] input").Change("2026-12-31");
        page.Find("[data-testid=confirm-leave]").Click();

        page.WaitForAssertion(() => _identity.Leaves.ShouldHaveSingleItem());
        _identity.Leaves[0].LeftOn.ShouldBe(new DateOnly(2026, 12, 31));
        _identity.Leaves[0].Version.ShouldBe("77");
    }

    [Fact]
    public void Employees_show_their_position_and_account()
    {
        var page = Render<EmployeesPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.Markup.ShouldContain("Tomas Radzevičius");
        page.Find("[data-testid=employee-account]").TextContent.ShouldBe("tomas.radzevicius");
        page.Markup.ShouldContain("No account");
    }
}

/// <summary>An <see cref="IdentityClient"/> with two positions and two employees.</summary>
public sealed class FakeOrganisationClient() : IdentityClient(new HttpClient())
{
    private static readonly PositionSummary Director = new(3, "COMMERCIAL_DIRECTOR", "Commercial director", null, [AppRoles.BrandManager, AppRoles.Sponsor], false, 1, "41");

    private static readonly PositionSummary Accountant = new(8, "ACCOUNTANT", "Accountant", null, [], false, 2, "42");

    private static readonly EmployeeSummary Tomas = new(Guid.NewGuid(), "E0001", "Tomas Radzevičius", "tomas@dealer.example", null, 3, "COMMERCIAL_DIRECTOR",
        "Commercial director", null, null, false, false, "tomas.radzevicius", "77");

    private static readonly EmployeeSummary Ona = new(Guid.NewGuid(), "E0101", "Ona Žilinskienė", null, null, 8, "ACCOUNTANT",
        "Accountant", null, null, false, false, null, "78");

    public List<(short Id, UpdatePositionRequest Request, string Version)> PositionUpdates { get; } = [];

    public int PositionsCreated { get; private set; }

    public ApiResult<PositionSummary>? NextPositionUpdate { get; set; }

    public List<CreateAccountRequest> Accounts { get; } = [];

    public List<(Guid Id, DateOnly LeftOn, string Version)> Leaves { get; } = [];

    public override Task<ApiResult<PagedResponse<PositionSummary>>> ListPositionsAsync(
        string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<PositionSummary>([Director, Accountant], page, pageSize, 2)));

    public override Task<ApiResult<PositionSummary>> CreatePositionAsync(CreatePositionRequest request, CancellationToken cancellationToken = default)
    {
        PositionsCreated++;
        return Task.FromResult(ApiResult.Success(Director));
    }

    public override Task<ApiResult<PositionSummary>> UpdatePositionAsync(short id, UpdatePositionRequest request, string version, CancellationToken cancellationToken = default)
    {
        PositionUpdates.Add((id, request, version));
        return Task.FromResult(NextPositionUpdate ?? ApiResult.Success(Director));
    }

    public override Task<ApiResult<PagedResponse<EmployeeSummary>>> ListEmployeesAsync(
        string? search, short? positionId, bool archived, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<EmployeeSummary>([Tomas, Ona], page, pageSize, 2)));

    public override Task<ApiResult<EmployeeSummary>> CreateAccountAsync(Guid id, CreateAccountRequest request, CancellationToken cancellationToken = default)
    {
        Accounts.Add(request);
        return Task.FromResult(ApiResult.Success(Ona));
    }

    public override Task<ApiResult<EmployeeSummary>> ArchiveEmployeeAsync(Guid id, ArchiveEmployeeRequest request, string version, CancellationToken cancellationToken = default)
    {
        Leaves.Add((id, request.LeftOn, version));
        return Task.FromResult(ApiResult.Success(Tomas));
    }
}
