using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Responsible employees of manufacturers (STAGE_7_PLAN 3.7, PR 7h4): the shared pattern for a third record kind. Its
/// one role, account manager, gives no rights, so the catalogue's edit permission is enough, nothing goes to Identity,
/// and the manufacturer cannot be archived while someone is responsible for it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ManufacturerResponsibleTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Planner = TestUser.Create("mresp.planner", [AppRoles.InventoryPlanner]);
    private static readonly TestUser Keeper = TestUser.Create("mresp.keeper", [AppRoles.WarehouseKeeper], warehouse: 3);

    private readonly string _tag = "MR" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_planner_names_an_account_manager_nothing_goes_to_Identity_and_the_manufacturer_cannot_be_archived()
    {
        var maker = await CreateAsync();
        var employee = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.account", [AppRoles.InventoryPlanner], []);
        var events = "SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.ResponsibilitiesChanged,%'";
        var before = await database.ScalarAsync<long>(events);

        using (var assigned = await SendAsync(Planner, HttpMethod.Put, $"/api/v1/inventory/manufacturers/{maker.Id}/responsibles",
                   new ResponsiblesRequest([new ResponsibleEntry(employee, "account_manager")])))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(Ct));
            var list = (await assigned.Content.ReadFromJsonAsync<List<ResponsibleDto>>(Ct))!;
            list.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Role.ShouldBe("ACCOUNT_MANAGER"), r => r.GrantsScope.ShouldBeFalse());
        }

        (await database.ScalarAsync<long>(events)).ShouldBe(before, "an account manager gets no rights, so Identity is not told");

        using var archive = await SendAsync(Planner, HttpMethod.Post, $"/api/v1/inventory/manufacturers/{maker.Id}/archive", null, maker.Version);
        archive.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await archive.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldBe(["1 responsible employee(s); remove them first"]);
    }

    [Fact]
    public async Task Every_role_reads_the_responsibles_and_a_brand_role_is_refused_for_a_manufacturer()
    {
        var maker = await CreateAsync();

        using (var read = await SendAsync(Keeper, HttpMethod.Get, $"/api/v1/inventory/manufacturers/{maker.Id}/responsibles"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var refused = await SendAsync(Keeper, HttpMethod.Put, $"/api/v1/inventory/manufacturers/{maker.Id}/responsibles", new ResponsiblesRequest([])))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var employee = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.wrong", [AppRoles.InventoryPlanner], []);
        using var wrongRole = await SendAsync(Planner, HttpMethod.Put, $"/api/v1/inventory/manufacturers/{maker.Id}/responsibles",
            new ResponsiblesRequest([new ResponsibleEntry(employee, "BRAND_MANAGER")]));
        wrongRole.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(wrongRole)).Code.ShouldBe("Inventory.UnknownAssignmentRole");
    }

    private async Task<ManufacturerDto> CreateAsync()
    {
        var code = _tag + Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        using var created = await SendAsync(Planner, HttpMethod.Post, "/api/v1/inventory/manufacturers", new ManufacturerWrite(code, $"Maker {code}", "DE", null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<ManufacturerDto>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body = null, string? version = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }
}
