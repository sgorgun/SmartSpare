using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The read-only Inventory API of stage 5, pulled forward (parts search and details, warehouses): the real single host on
/// PostgreSQL 17 as <c>smartspare_api</c>, called with internal tokens as the gateway would call a service.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class InventoryApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Planner = TestUser.Create("inventory.planner", [AppRoles.InventoryPlanner]);

    /// <summary>A prefix no other test uses, so the shared database can hold other parts.</summary>
    private readonly string _tag = "ZQ" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    public static TheoryData<string> Roles => [.. AppRoles.All];

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Search_matches_the_part_number_or_the_description_case_insensitively_one_page_at_a_time()
    {
        for (var i = 1; i <= 3; i++)
        {
            await AddPartAsync($"{_tag}-00{i}", $"Hydraulic filter {i}");
        }

        await AddPartAsync($"{_tag}-100", $"Seal kit for {_tag.ToLowerInvariant()} pumps");

        var first = await SearchAsync($"?search={_tag.ToLowerInvariant()}&page=1&pageSize=2");
        first.TotalCount.ShouldBe(4, "three by part number, one by description");
        first.Page.ShouldBe(1);
        first.PageSize.ShouldBe(2);
        first.Items.Select(p => p.OemPartNumber).ShouldBe([$"{_tag}-001", $"{_tag}-002"], "ordered by OEM part number");

        var last = await SearchAsync($"?search={_tag}&page=2&pageSize=2");
        last.Items.Select(p => p.OemPartNumber).ShouldBe([$"{_tag}-003", $"{_tag}-100"]);
        var part = last.Items[0];
        (part.BrandCode, part.CategoryName, part.UnitOfMeasure, part.UnitCost).ShouldBe(("JD", "Engine oil filters", "PCS", 18.40m));

        (await SearchAsync($"?search={_tag}&page=3&pageSize=2")).Items.ShouldBeEmpty("a page past the end is empty, not an error");
    }

    [Fact]
    public async Task Like_wildcards_in_the_search_match_literally()
    {
        await AddPartAsync($"{_tag}-200", "Clamp");

        (await SearchAsync($"?search={_tag}_200")).TotalCount.ShouldBe(0, "_ is not a wildcard");
        (await SearchAsync($"?search={_tag}%25200")).TotalCount.ShouldBe(0, "% is not a wildcard");
        (await SearchAsync($"?search={_tag}-200")).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Deactivated_parts_are_not_listed_but_can_be_read_by_id()
    {
        var id = await AddPartAsync($"{_tag}-300", "Old belt", active: false);

        (await SearchAsync($"?search={_tag}-300")).TotalCount.ShouldBe(0);

        var details = await GetAsync<PartDetails>($"/api/v1/inventory/parts/{id}");
        details.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task A_part_comes_with_its_reference_data_and_its_stock_per_warehouse()
    {
        var id = await AddPartAsync($"{_tag}-400", "Engine oil filter");
        await AddStockAsync(id, "KLP", 24);
        await AddStockAsync(id, "PNV", 2);

        var part = await GetAsync<PartDetails>($"/api/v1/inventory/parts/{id}");

        part.Id.ShouldBe(id);
        (part.BrandCode, part.BrandName, part.CategoryCode, part.UnitOfMeasure).ShouldBe(("JD", "John Deere", "OIL_FILTER", "PCS"));
        part.IsActive.ShouldBeTrue();
        part.Stock.Select(s => (s.WarehouseCode, s.OnHand, s.Reserved, s.Available)).ShouldBe([("KLP", 24m, 0m, 24m), ("PNV", 2m, 0m, 2m)]);
    }

    [Fact]
    public async Task An_unknown_part_is_a_404_problem_with_a_stable_code()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync("/api/v1/inventory/parts/999999999", bearer: _host.Token(Planner.Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await Problem.ReadAsync(response);
        problem.Code.ShouldBe("Inventory.PartNotFound");
        problem.Detail.ShouldBe("Part 999999999 does not exist.");
        problem.TraceId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_warehouses_are_listed_central_hub_first()
    {
        var warehouses = await GetAsync<List<WarehouseResponse>>("/api/v1/inventory/warehouses");

        warehouses.Count.ShouldBe(7);
        warehouses[0].Code.ShouldBe("VLN");
        warehouses[0].IsCentralHub.ShouldBeTrue();
        warehouses.Skip(1).Select(w => w.Code).ShouldBe(["KLP", "KNS", "MRJ", "PNV", "SIA", "UTN"]);
    }

    [Theory]
    [InlineData("?pageSize=500")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=0")]
    public async Task Invalid_paging_is_a_validation_problem(string query)
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync($"/api/v1/inventory/parts{query}", bearer: _host.Token(Planner.Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(response)).Code.ShouldBe("Catalog.InvalidPaging", "the parts list is the shared catalogue list (PR 7i1)");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Every_role_may_read_inventory(string role)
    {
        using var browser = _host.NewBrowser();
        var user = TestUser.Create($"reader.{role.ToLowerInvariant()}", [role]);

        foreach (var path in new[] { "/api/v1/inventory/parts", "/api/v1/inventory/warehouses" })
        {
            using var response = await browser.GetAsync(path, bearer: _host.Token(user.Principal()));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{role} on {path}");
        }
    }

    [Fact]
    public async Task Anonymous_gets_401_and_a_user_without_a_role_gets_403()
    {
        using var browser = _host.NewBrowser();

        using var anonymous = await browser.GetAsync("/api/v1/inventory/parts");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Problem.ReadAsync(anonymous)).Code.ShouldBe(ProblemCodes.Unauthenticated);

        var noRole = TestUser.Create("no.role", []);
        using var forbidden = await browser.GetAsync("/api/v1/inventory/warehouses", bearer: _host.Token(noRole.Principal()));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem.ReadAsync(forbidden)).Code.ShouldBe(ProblemCodes.Forbidden);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<PagedResponse<PartDto>> SearchAsync(string query) =>
        GetAsync<PagedResponse<PartDto>>("/api/v1/inventory/parts" + query);

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: _host.Token(Planner.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private Task<long> AddPartAsync(string oemPartNumber, string description, bool active = true) =>
        database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost, is_archived, archived_at)
            SELECT b.id, @oem, @description, c.id, u.id, 18.40, NOT @active, CASE WHEN @active THEN NULL ELSE now() END
            FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """,
            ("oem", oemPartNumber), ("description", description), ("active", active));

    /// <summary>An opening balance through the stock ledger; the balance follows by trigger.</summary>
    private Task AddStockAsync(long partId, string warehouseCode, decimal quantity) =>
        database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id)
            SELECT w.id, @part FROM inventory.warehouse w WHERE w.code = @warehouse;
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            SELECT now(), w.id, @part, 'OPENING_BALANCE', @quantity, 18.40, 'Test stock'
            FROM inventory.warehouse w WHERE w.code = @warehouse;
            """,
            ("part", partId), ("warehouse", warehouseCode), ("quantity", quantity));

}
