using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The parts catalogue on the shared mechanism (STAGE_7_PLAN 3.1, PR 7i1): the OEM number is unique per brand, the brand
/// is fixed, every write reaches Fleet's part copy, an archived manufacturer cannot be chosen (SS500, refused by the
/// database), the manufacturer is cleared in the editor, and a part with stock cannot be archived (SS501 with reasons).
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class PartCatalogTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Parts = "/api/v1/inventory/parts";

    private static readonly TestUser Planner = TestUser.Create("parts.planner", [AppRoles.InventoryPlanner]);
    private static readonly TestUser Keeper = TestUser.Create("parts.keeper", [AppRoles.WarehouseKeeper], warehouse: 3);

    private readonly string _tag = "P" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private short _jd;
    private short _claas;
    private int _category;
    private short _pcs;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _jd = await database.ScalarAsync<short>("SELECT id FROM catalog.brand WHERE code = 'JD'");
        _claas = await database.ScalarAsync<short>("SELECT id FROM catalog.brand WHERE code = 'CLAAS'");
        _category = await database.ScalarAsync<int>("SELECT id FROM ref.part_category WHERE code = 'OIL_FILTER'");
        _pcs = await database.ScalarAsync<short>("SELECT id FROM ref.unit_of_measure WHERE code = 'PCS'");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_part_is_unique_per_brand_reaches_fleet_and_its_manufacturer_is_cleared_in_the_editor()
    {
        var maker = await MakerAsync("M");
        var part = await CreateAsync(Write($"{_tag}-1", _jd, maker));
        (part.OemPartNumber, part.BrandCode, part.ManufacturerCode).ShouldBe(($"{_tag}-1", "JD", $"{_tag}M"));
        await EventuallyAsync(async () => await FleetDescriptionAsync(part.Id) == "Engine oil filter");

        using (var duplicate = await SendAsync(Planner, HttpMethod.Post, Parts, Write($"{_tag.ToLowerInvariant()}-1", _jd, null)))
        {
            duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the same OEM number twice for one brand");
        }

        (await CreateAsync(Write($"{_tag}-1", _claas, null))).BrandCode.ShouldBe("CLAAS", "another brand may use the number");

        using (var edited = await SendAsync(Planner, HttpMethod.Put, $"{Parts}/{part.Id}", Write($"{_tag}-1", _jd, null) with { Name = "Engine oil filter, long life" }, part.Version))
        {
            edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
            var dto = (await edited.Content.ReadFromJsonAsync<PartDto>(Ct))!;
            (dto.ManufacturerId, dto.Description).ShouldBe(((short?)null, "Engine oil filter, long life"));
            part = dto;
        }

        await EventuallyAsync(async () => await FleetDescriptionAsync(part.Id) == "Engine oil filter, long life");

        using var otherBrand = await SendAsync(Planner, HttpMethod.Put, $"{Parts}/{part.Id}", Write($"{_tag}-1", _claas, null), part.Version);
        otherBrand.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(otherBrand)).Code.ShouldBe("Inventory.PartBrandFixed");
    }

    [Fact]
    public async Task An_archived_manufacturer_cannot_be_chosen_and_the_database_refuses_it_too()
    {
        var maker = await MakerAsync("A");
        await database.ExecuteAsync("UPDATE catalog.manufacturer SET is_archived = true, archived_at = now() WHERE id = @id", ("id", maker));

        using var refused = await SendAsync(Planner, HttpMethod.Post, Parts, Write($"{_tag}-2", _jd, maker));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("SS500");
    }

    [Fact]
    public async Task A_part_with_stock_cannot_be_archived_and_an_unused_one_leaves_the_list()
    {
        var stocked = await CreateAsync(Write($"{_tag}-3", _jd, null));
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id) SELECT w.id, @part FROM inventory.warehouse w WHERE w.code = 'KLP';
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            SELECT now(), w.id, @part, 'OPENING_BALANCE', 4, 18.40, 'Test stock' FROM inventory.warehouse w WHERE w.code = 'KLP';
            """,
            ("part", stocked.Id));

        using (var blocked = await SendAsync(Planner, HttpMethod.Post, $"{Parts}/{stocked.Id}/archive", null, stocked.Version))
        {
            blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await blocked.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
            body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldBe(["on hand 4 at KLP"]);
        }

        var unused = await CreateAsync(Write($"{_tag}-4", _jd, null));
        using (var archived = await SendAsync(Planner, HttpMethod.Post, $"{Parts}/{unused.Id}/archive", null, unused.Version))
        {
            archived.StatusCode.ShouldBe(HttpStatusCode.OK, await archived.Content.ReadAsStringAsync(Ct));
        }

        using var list = await SendAsync(Planner, HttpMethod.Get, $"{Parts}?search={_tag}-");
        var page = (await list.Content.ReadFromJsonAsync<PagedResponse<PartDto>>(Ct))!;
        page.Items.Select(p => p.OemPartNumber).ShouldBe([$"{_tag}-3"], "archived parts leave the active list and the pickers");
    }

    [Fact]
    public async Task A_keeper_reads_the_catalogue_but_cannot_add_parts()
    {
        using (var read = await SendAsync(Keeper, HttpMethod.Get, $"{Parts}/lookup?search=RE"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var refused = await SendAsync(Keeper, HttpMethod.Post, Parts, Write($"{_tag}-5", _jd, null));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private PartWrite Write(string oem, short brand, short? maker) =>
        new(oem, "Engine oil filter", brand, _category, _pcs, 0.45m, 18.40m, false, maker);

    private async Task<short> MakerAsync(string suffix)
    {
        using var created = await SendAsync(Planner, HttpMethod.Post, "/api/v1/inventory/manufacturers", new ManufacturerWrite(_tag + suffix, $"Maker {suffix}", "DE", null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<ManufacturerDto>(Ct))!.Id;
    }

    private async Task<PartDto> CreateAsync(PartWrite write)
    {
        using var created = await SendAsync(Planner, HttpMethod.Post, Parts, write);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<PartDto>(Ct))!;
    }

    private Task<string> FleetDescriptionAsync(long partId) =>
        database.ScalarAsync<string>("SELECT coalesce((SELECT description FROM fleet.part_snapshot WHERE part_id = @id), '')", ("id", partId));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !await condition())
        {
            await Task.Delay(200, Ct);
        }

        (await condition()).ShouldBeTrue("the event reached Fleet's part copy within 30 s");
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
