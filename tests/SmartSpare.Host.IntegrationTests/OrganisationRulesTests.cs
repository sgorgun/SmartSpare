using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Organisation rules on the T001 demo database (ADR 0026, STAGE_7_PLAN 1.2 and 1.4): T001 has exactly one
/// administrator, so the last-administrator rule is visible; an employee who leaves can no longer sign in, at once.
/// Only refused changes touch the demo users, so the golden access matrix of the same database stays valid.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DemoDatabaseGroup.Name)]
public sealed class OrganisationRulesTests(DemoDatabaseFixture database) : IAsyncLifetime
{
    private const string AdminEmployee = "00000000-0000-4000-8000-000000000011";

    private HostFactory _host = null!;

    /// <summary>An administrator principal that is not the demo administrator (its id has no account).</summary>
    private readonly TestUser _admin = TestUser.Create("rules.admin", [AppRoles.Administrator]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_last_administrator_can_neither_leave_nor_lose_the_role()
    {
        var employee = await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{AdminEmployee}");
        using (var leave = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{AdminEmployee}/archive",
                   new ArchiveEmployeeRequest(new DateOnly(2026, 12, 31)), employee.Version))
        {
            leave.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(leave)).Code.ShouldBe("Identity.LastAdministrator");
        }

        var position = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{employee.PositionId}");
        using (var demote = await SendAsync(HttpMethod.Put, $"/api/v1/identity/positions/{position.Id}",
                   new UpdatePositionRequest(position.Name, position.Description, [AppRoles.InventoryPlanner]), position.Version))
        {
            demote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(demote)).Code.ShouldBe("Identity.LastAdministrator");
        }

        (await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{AdminEmployee}")).IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task An_employee_who_leaves_cannot_sign_in_at_once_and_the_open_session_ends_at_the_next_check()
    {
        var keepers = (await GetAsync<PagedResponse<PositionSummary>>("/api/v1/identity/positions?search=WAREHOUSE_KEEPER")).Items.Single();
        EmployeeSummary employee;
        using (var created = await SendAsync(HttpMethod.Post, "/api/v1/identity/employees",
                   new CreateEmployeeRequest("E0901", "Leaving Keeper", null, null, keepers.Id, new DateOnly(2026, 1, 1))))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            employee = (await created.Content.ReadFromJsonAsync<EmployeeSummary>(Ct))!;
        }

        using (var account = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{employee.Id}/account",
                   new CreateAccountRequest("leaving.keeper", DemoDatabaseFixture.Password)))
        {
            account.StatusCode.ShouldBe(HttpStatusCode.Created, await account.Content.ReadAsStringAsync(Ct));
        }

        using var browser = _host.NewBrowser();
        using (var login = await browser.LoginAsync("leaving.keeper", DemoDatabaseFixture.Password))
        {
            login.IsSuccessStatusCode.ShouldBeTrue(await login.Content.ReadAsStringAsync(Ct));
        }

        (await browser.MeAsync()).Roles.ShouldBe([AppRoles.WarehouseKeeper], "the account has the roles of its position");

        var version = (await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{employee.Id}")).Version;
        using (var left = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{employee.Id}/archive",
                   new ArchiveEmployeeRequest(new DateOnly(2026, 12, 31)), version))
        {
            left.StatusCode.ShouldBe(HttpStatusCode.OK, await left.Content.ReadAsStringAsync(Ct));
        }

        using (var second = _host.NewBrowser())
        using (var refused = await second.LoginAsync("leaving.keeper", DemoDatabaseFixture.Password))
        {
            refused.IsSuccessStatusCode.ShouldBeFalse("an employee who left is refused at sign-in at once");
        }

        _host.Time.Advance(BffDefaults.SessionCheckInterval);
        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse("the open session ends at the next session check");
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: _host.Token(_admin.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, string? ifMatch = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(_admin.Principal()));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }
}
