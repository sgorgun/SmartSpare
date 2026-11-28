using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The brand catalogue (STAGE_7_PLAN 3.1, PR 7h2): every write, single or by CSV, publishes BrandCatalogChanged in its
/// transaction, so Fleet's and Identity's copies follow; a brand with active parts or responsible employees cannot be
/// archived (SS501 with the reasons); only planners and administrators change it. Single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class BrandCatalogTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Brands = "/api/v1/inventory/brands";

    private static readonly TestUser Planner = TestUser.Create("brands.planner", [AppRoles.InventoryPlanner]);
    private static readonly TestUser Admin = TestUser.Create("brands.admin", [AppRoles.Administrator]);
    private static readonly TestUser Keeper = TestUser.Create("brands.keeper", [AppRoles.WarehouseKeeper], warehouse: 3);

    private readonly string _tag = "K" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_created_and_renamed_brand_reaches_both_copies_with_its_id()
    {
        using var created = await SendAsync(Planner, HttpMethod.Post, Brands, new BrandWrite($"{_tag}a", "New brand", "de"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var brand = (await created.Content.ReadFromJsonAsync<BrandDto>(Ct))!;
        (brand.Code, brand.CountryCode).ShouldBe(($"{_tag}A", "DE"));
        await EventuallyAsync(async () => await CopiesAsync(brand.Id) == ("New brand", "New brand"));

        using (var renamed = await SendAsync(Planner, HttpMethod.Put, $"{Brands}/{brand.Id}", new BrandWrite(brand.Code, "Renamed brand", "DE"), brand.Version))
        {
            renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventuallyAsync(async () => await CopiesAsync(brand.Id) == ("Renamed brand", "Renamed brand"));
    }

    [Fact]
    public async Task A_planner_edits_the_brand_record_but_only_an_administrator_assigns_its_brand_manager()
    {
        var brand = await CreateAsync("M");
        var employee = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.bm", [AppRoles.BrandManager], []);
        var assignment = new ResponsiblesRequest([new ResponsibleEntry(employee, "BRAND_MANAGER")]);

        using (var edited = await SendAsync(Planner, HttpMethod.Put, $"{Brands}/{brand.Id}", new BrandWrite(brand.Code, "Edited by a planner", "DE"), brand.Version))
        {
            edited.StatusCode.ShouldBe(HttpStatusCode.OK, "Inventory.Brand.Edit");
        }

        using (var refused = await SendAsync(Planner, HttpMethod.Put, $"{Brands}/{brand.Id}/responsibles", assignment))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a brand manager gets approval rights: Inventory.Brand.ManageAssignments");
        }

        using var granted = await SendAsync(Admin, HttpMethod.Put, $"{Brands}/{brand.Id}/responsibles", assignment);
        granted.StatusCode.ShouldBe(HttpStatusCode.OK, await granted.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_brand_with_active_parts_or_responsibles_cannot_be_archived_and_an_unused_one_is_archived_in_the_copies_too()
    {
        var used = await CreateAsync("U");
        await database.ExecuteAsync(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT @brand, @oem, 'Part of a used brand', c.id, u.id, 1 FROM ref.part_category c, ref.unit_of_measure u
            WHERE c.code = 'OIL_FILTER' AND u.code = 'PCS'
            """,
            ("brand", used.Id), ("oem", $"{_tag}-USED-1"));
        var employee = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.manager", [AppRoles.BrandManager], []);
        using (var assigned = await SendAsync(Admin, HttpMethod.Put, $"{Brands}/{used.Id}/responsibles", new ResponsiblesRequest([new ResponsibleEntry(employee, "BRAND_MANAGER")])))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(Ct));
        }

        using (var refused = await SendAsync(Planner, HttpMethod.Post, $"{Brands}/{used.Id}/archive", null, used.Version))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
            body.GetProperty("code").GetString().ShouldBe("SS501");
            var reasons = body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
            reasons.ShouldBe(["1 active part(s)", "1 responsible employee(s); remove them on the Responsibilities page first"]);
        }

        var unused = await CreateAsync("F");
        await EventuallyAsync(async () => await ArchivedInCopiesAsync(unused.Id) == (false, false));
        using (var archived = await SendAsync(Planner, HttpMethod.Post, $"{Brands}/{unused.Id}/archive", null, unused.Version))
        {
            archived.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventuallyAsync(async () => await ArchivedInCopiesAsync(unused.Id) == (true, true));
    }

    [Fact]
    public async Task A_csv_import_publishes_the_brands_it_wrote_and_its_dry_run_publishes_nothing()
    {
        var csv = $"code,name,country_code\n{_tag}C1,Imported one,DE\n{_tag}C2,Imported two,IT\n";
        var outbox = "SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.BrandCatalogChanged,%'";
        var before = await database.ScalarAsync<long>(outbox);

        (await ImportAsync(csv, dryRun: true)).Created.ShouldBe(2);
        (await database.ScalarAsync<long>(outbox)).ShouldBe(before, "the dry run's event was rolled back with it");

        (await ImportAsync(csv, dryRun: false)).Applied.ShouldBeTrue();
        var ids = await database.ScalarAsync<short[]>("SELECT array_agg(id ORDER BY code) FROM catalog.brand WHERE code LIKE @p", ("p", $"{_tag}C%"));
        await EventuallyAsync(async () => await CopiesAsync(ids[0]) == ("Imported one", "Imported one") && await CopiesAsync(ids[1]) == ("Imported two", "Imported two"));
    }

    [Fact]
    public async Task Every_role_reads_brands_and_only_planners_and_administrators_change_them()
    {
        using (var read = await SendAsync(Keeper, HttpMethod.Get, $"{Brands}?search=CLAAS"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await read.Content.ReadFromJsonAsync<PagedResponse<BrandDto>>(Ct))!.Items.ShouldContain(b => b.Code == "CLAAS");
        }

        using var refused = await SendAsync(Keeper, HttpMethod.Post, Brands, new BrandWrite($"{_tag}k", "Keeper's brand", "DE"));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<BrandDto> CreateAsync(string suffix)
    {
        using var created = await SendAsync(Planner, HttpMethod.Post, Brands, new BrandWrite(_tag + suffix, $"Brand {_tag}{suffix}", "DE"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<BrandDto>(Ct))!;
    }

    private async Task<CatalogImportResult> ImportAsync(string csv, bool dryRun)
    {
        using var browser = _host.NewBrowser();
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "brands.csv" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Brands}/csv?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Planner.Principal()));
        using var response = await browser.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CatalogImportResult>(Ct))!;
    }

    private async Task<(string? Fleet, string? Identity)> CopiesAsync(short id) => (
        await database.ScalarAsync<string?>("SELECT name::text FROM fleet.brand_snapshot WHERE brand_id = @id", ("id", id)),
        await database.ScalarAsync<string?>("SELECT name::text FROM iam.brand_snapshot WHERE brand_id = @id", ("id", id)));

    private async Task<(bool? Fleet, bool? Identity)> ArchivedInCopiesAsync(short id) => (
        await database.ScalarAsync<bool?>("SELECT is_archived FROM fleet.brand_snapshot WHERE brand_id = @id", ("id", id)),
        await database.ScalarAsync<bool?>("SELECT is_archived FROM iam.brand_snapshot WHERE brand_id = @id", ("id", id)));

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

    /// <summary>The single host delivers through the outbox dispatcher's polling interval; waits up to 30 s.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        (await condition()).ShouldBeTrue("the event reached the consumers within 30 s");
    }
}
