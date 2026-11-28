using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The shared master-data mechanism (ADR 0025, STAGE_7_PLAN 2) tested once on a real catalogue, the adjustment reasons:
/// search and paging, ETag and If-Match, the fixed code, archive and restore, SS500 on an archived reason, the history
/// in plain words, and the policies. Single host and PostgreSQL 17 as <c>smartspare_api</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class CatalogApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Reasons = "/api/v1/inventory/adjustment-reasons";

    /// <summary>A code prefix no other test uses, so searches see only this test's reasons.</summary>
    private readonly string _tag = "CT" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private TestUser _planner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var plannerId = await database.CreateUserAsync($"catalog.planner.{_tag.ToLowerInvariant()}", [AppRoles.InventoryPlanner], []);
        _planner = new TestUser(plannerId, "catalog.planner", "unused", [AppRoles.InventoryPlanner], []);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_planner_creates_searches_and_reads_a_reason_with_its_version()
    {
        using var browser = _host.NewBrowser();
        using var created = await browser.PostAsync(Reasons, Write("_SCRAP", "Scrapped at receipt", "out"), bearer: Token(_planner));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var reason = (await created.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!;
        (reason.Code, reason.Direction, reason.IsArchived).ShouldBe(($"{_tag}_SCRAP", AdjustmentDirections.Out, false), "codes and directions are stored in capitals");
        created.Headers.Location!.OriginalString.ShouldBe($"{Reasons}/{reason.Id}");
        created.Headers.ETag!.Tag.ShouldBe($"\"{reason.Version}\"");

        await CreateAsync("_FOUND", "Found in the yard", AdjustmentDirections.In);
        var page = await GetAsync<PagedResponse<AdjustmentReasonDto>>($"{Reasons}?search={_tag}&sort=-code&pageSize=1");
        page.TotalCount.ShouldBe(2);
        page.Items.ShouldHaveSingleItem().Code.ShouldBe($"{_tag}_SCRAP", "sorted by code descending, one per page");

        using var duplicate = await browser.PostAsync(Reasons, Write("_SCRAP", "Again", AdjustmentDirections.Out), bearer: Token(_planner));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(duplicate)).Code.ShouldBe("Catalog.CodeTaken");

        using var invalid = await browser.PostAsync(Reasons, Write("_X", string.Empty, "SIDEWAYS"), bearer: Token(_planner));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the ApiContracts validator runs on the server too");
    }

    [Fact]
    public async Task Updates_need_the_version_read_and_keep_the_code()
    {
        var reason = await CreateAsync("_WET", "Water damage", AdjustmentDirections.Out);

        using (var noVersion = await PutAsync(reason.Id, Write("_WET", "Water damage in storage", AdjustmentDirections.Out), ifMatch: null, _planner))
        {
            noVersion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(noVersion)).Code.ShouldBe("Catalog.VersionRequired");
        }

        AdjustmentReasonDto changed;
        using (var ok = await PutAsync(reason.Id, Write("_WET", "Water damage in storage", AdjustmentDirections.Both), $"\"{reason.Version}\"", _planner))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
            changed = (await ok.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!;
            (changed.Name, changed.Direction).ShouldBe(("Water damage in storage", AdjustmentDirections.Both));
            changed.Version.ShouldNotBe(reason.Version);
        }

        using (var stale = await PutAsync(reason.Id, Write("_WET", "Lost edit", AdjustmentDirections.Out), $"\"{reason.Version}\"", _planner))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the version read is no longer current");
        }

        using (var recode = await PutAsync(reason.Id, Write("_DAMP", "Water damage in storage", AdjustmentDirections.Both), $"\"{changed.Version}\"", _planner))
        {
            recode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(recode)).Code.ShouldBe("Catalog.CodeFixed");
        }

        var administrator = TestUser.Create("catalog.admin", [AppRoles.Administrator]);
        using var adminRecode = await PutAsync(reason.Id, Write("_DAMP", "Water damage in storage", AdjustmentDirections.Both), $"\"{changed.Version}\"", administrator);
        adminRecode.StatusCode.ShouldBe(HttpStatusCode.OK, "an administrator may correct a code");
        (await adminRecode.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!.Code.ShouldBe($"{_tag}_DAMP");
    }

    [Fact]
    public async Task An_archived_reason_leaves_the_lookup_and_cannot_be_chosen_until_restored()
    {
        var reason = await CreateAsync("_THEFT", "Theft", AdjustmentDirections.Out);
        var klp = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'KLP'");
        var part = await StockedPartAsync(klp);
        var keeper = TestUser.Create("catalog.keeper", [AppRoles.WarehouseKeeper], warehouse: klp);
        using var browser = _host.NewBrowser();

        using (var archived = await PostStateAsync(reason.Id, "archive", reason.Version))
        {
            archived.StatusCode.ShouldBe(HttpStatusCode.OK, await archived.Content.ReadAsStringAsync(Ct));
            reason = (await archived.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!;
            reason.IsArchived.ShouldBeTrue();
        }

        (await GetAsync<List<CatalogLookupItem>>($"{Reasons}/lookup?search={_tag}")).ShouldBeEmpty("archived entries cannot be chosen");
        (await GetAsync<PagedResponse<AdjustmentReasonDto>>($"{Reasons}?search={_tag}&archived=true")).Items.ShouldHaveSingleItem();
        using (var refused = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, -1, (short)reason.Id, null), bearer: Token(keeper)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(refused)).Code.ShouldBe("SS500");
        }

        using (var restored = await PostStateAsync(reason.Id, "restore", reason.Version))
        {
            restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var allowed = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
            new StockAdjustmentRequest(klp, part, -1, (short)reason.Id, null), bearer: Token(keeper));
        allowed.StatusCode.ShouldBe(HttpStatusCode.Created, await allowed.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_history_says_who_changed_what_and_when()
    {
        var reason = await CreateAsync("_HIST", "Broken seal", AdjustmentDirections.Out);
        using (var renamed = await PutAsync(reason.Id, Write("_HIST", "Broken seal at receipt", AdjustmentDirections.Out), $"\"{reason.Version}\"", _planner))
        {
            reason = (await renamed.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!;
        }

        (await PostStateAsync(reason.Id, "archive", reason.Version)).Dispose();

        var history = await GetAsync<PagedResponse<RecordHistoryEntry>>($"{Reasons}/{reason.Id}/history");

        history.TotalCount.ShouldBe(3);
        history.Items.Select(e => e.Action).ShouldBe(["archived", "changed", "created"], "newest first");
        history.Items.ShouldAllBe(e => e.Actor == "Test U.", "the employee behind the account, first name and initial");
        history.Items[1].Changes.ShouldHaveSingleItem().ShouldBe(new RecordChange("Name", "Broken seal", "Broken seal at receipt"));
        history.Items[2].Changes.ShouldContain(new RecordChange("Direction", null, AdjustmentDirections.Out));

        using var browser = _host.NewBrowser();
        using var missing = await browser.GetAsync($"{Reasons}/32000/history", bearer: Token(_planner));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_role_reads_the_catalogue_but_only_planners_and_administrators_change_it()
    {
        var keeper = TestUser.Create("catalog.reader", [AppRoles.WarehouseKeeper]);
        using var browser = _host.NewBrowser();

        using (var read = await browser.GetAsync($"{Reasons}/lookup", bearer: Token(keeper)))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await read.Content.ReadFromJsonAsync<List<CatalogLookupItem>>(Ct))!.Select(r => r.Code)
                .ShouldContain("COUNT_CORRECTION", "the standard reasons are seeded");
        }

        using var write = await browser.PostAsync(Reasons, Write("_NOPE", "Not allowed", AdjustmentDirections.In), bearer: Token(keeper));
        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private AdjustmentReasonWrite Write(string suffix, string name, string direction) => new(_tag + suffix, name, direction, false);

    private async Task<AdjustmentReasonDto> CreateAsync(string suffix, string name, string direction)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.PostAsync(Reasons, Write(suffix, name, direction), bearer: Token(_planner));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AdjustmentReasonDto>(Ct))!;
    }

    private async Task<HttpResponseMessage> PutAsync(long id, AdjustmentReasonWrite body, string? ifMatch, TestUser user)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{Reasons}/{id}", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> PostStateAsync(long id, string action, string version)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Reasons}/{id}/{action}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(_planner));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(_planner));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    /// <summary>A part with stock at the hub, so a shortage can be posted.</summary>
    private async Task<long> StockedPartAsync(short warehouseId)
    {
        var part = await database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Catalogue test part', c.id, u.id, 5
            FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """,
            ("oem", $"{_tag}-P1"));
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id, safety_stock_qty) VALUES (@warehouse, @part, 0);
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            VALUES (now(), @warehouse, @part, 'OPENING_BALANCE', 5, 5, 'Test stock');
            """,
            ("warehouse", warehouseId), ("part", part));
        return part;
    }

    private string Token(TestUser user) => _host.Token(user.Principal());
}
