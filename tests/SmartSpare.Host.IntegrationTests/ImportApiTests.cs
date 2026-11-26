using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The CSV import of stage 5 (STAGE_5_PLAN section 5): raw → staging → core in one transaction with the 5 % quality gate,
/// duplicate files, header errors, the policy and the upload rate limit, on the real single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ImportApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string CatalogHeader = "brand_code,oem_part_number,description,category_code,uom_code,weight_kg,unit_cost,condition_based";

    private static readonly TestUser Planner = TestUser.Create("import.planner", [AppRoles.InventoryPlanner]);

    private readonly string _tag = "IM" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_part_catalogue_is_loaded_and_its_parts_can_be_searched()
    {
        var file = CatalogHeader + "\n"
            + $"JD,{_tag}-001,Hydraulic filter,OIL_FILTER,PCS,0.45,18.40,no\n"
            + $"jd, {_tag}-002 ,Seal kit for pumps,OIL_FILTER,pcs,,7.10,yes\n";

        using var response = await UploadAsync(Planner, ImportSources.PartCatalog, file);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var details = (await response.Content.ReadFromJsonAsync<ImportBatchDetails>(Ct))!;
        (details.Batch.Status, details.Batch.TotalRows, details.Batch.ValidRows, details.Batch.LoadedRows)
            .ShouldBe((ImportStatuses.Loaded, 2, (int?)2, (int?)2));
        details.Errors.ShouldBeEmpty();
        details.Batch.UploadedBy.ShouldBe(Planner.Id);
        response.Headers.Location!.ToString().ShouldEndWith($"/api/v1/inventory/imports/{details.Batch.Id}");

        var parts = await GetAsync<PagedResponse<PartSummary>>($"/api/v1/inventory/parts?search={_tag}");
        parts.Items.Select(p => p.OemPartNumber).ShouldBe([$"{_tag}-001", $"{_tag}-002"], "normalised to upper case without spaces");
    }

    [Fact]
    public async Task More_than_five_percent_invalid_rows_load_nothing_and_report_every_error()
    {
        var file = CatalogHeader + "\n"
            + $"JD,{_tag}-101,Valid part,OIL_FILTER,PCS,,10,no\n"
            + $"XX,{_tag}-102,Unknown brand,OIL_FILTER,PCS,,10,no\n"
            + $"JD,{_tag}-103,Bad cost,OIL_FILTER,PCS,,ten,no\n";

        using var response = await UploadAsync(Planner, ImportSources.PartCatalog, file);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var details = (await response.Content.ReadFromJsonAsync<ImportBatchDetails>(Ct))!;
        details.Batch.Status.ShouldBe(ImportStatuses.Failed);
        details.Batch.LoadedRows.ShouldBe(0);
        details.Batch.Message.ShouldNotBeNull().ShouldContain("nothing loaded");
        details.Errors.Select(e => (e.LineNo, e.Column, e.Code)).ShouldBe([(3, "brand_code", "UNKNOWN_REF"), (4, "unit_cost", "TYPE")]);
        details.ErrorCount.ShouldBe(2);
        (await GetAsync<PagedResponse<PartSummary>>($"/api/v1/inventory/parts?search={_tag}-10")).TotalCount.ShouldBe(0);

        var again = await GetAsync<ImportBatchDetails>($"/api/v1/inventory/imports/{details.Batch.Id}");
        again.ShouldBeEquivalentTo(details);
        var history = await GetAsync<PagedResponse<ImportBatchResponse>>("/api/v1/inventory/imports?pageSize=100");
        history.Items.ShouldContain(b => b.Id == details.Batch.Id);
    }

    [Fact]
    public async Task Demand_history_is_loaded_after_its_catalogue_and_a_file_is_imported_only_once()
    {
        using (var catalogue = await UploadAsync(Planner, ImportSources.PartCatalog, CatalogHeader + $"\nJD,{_tag}-201,Demand part,OIL_FILTER,PCS,,5,no\n"))
        {
            catalogue.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var demand = "doc_date,warehouse_code,brand_code,oem_part_number,quantity,doc_ref\n"
            + $"2026-10-01,KLP,JD,{_tag}-201,2,INV-{_tag}-1\n"
            + $"2026-10-08,PNV,JD,{_tag}-201,1.5,INV-{_tag}-2\n";
        using (var first = await UploadAsync(Planner, ImportSources.DemandHistory, demand))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
            var details = (await first.Content.ReadFromJsonAsync<ImportBatchDetails>(Ct))!;
            (details.Batch.Status, details.Batch.LoadedRows).ShouldBe((ImportStatuses.Loaded, (int?)2));
        }

        (await database.ScalarAsync<decimal>(
            """
            SELECT sum(m.quantity) FROM inventory.stock_movement m JOIN catalog.part p ON p.id = m.part_id
            WHERE p.oem_part_number = @oem AND m.movement_type_code = 'HISTORICAL_DEMAND'
            """,
            ("oem", $"{_tag}-201"))).ShouldBe(3.5m);

        using var duplicate = await UploadAsync(Planner, ImportSources.DemandHistory, demand);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(duplicate)).Code.ShouldBe("Inventory.ImportDuplicate");
    }

    [Fact]
    public async Task A_wrong_header_is_a_validation_problem_and_stores_nothing()
    {
        using var response = await UploadAsync(Planner, ImportSources.PartCatalog, "brand,part\nJD,X\n");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(response)).Code.ShouldBe("Inventory.ImportHeader");
    }

    [Theory]
    [InlineData(AppRoles.WarehouseKeeper)]
    [InlineData(AppRoles.BrandManager)]
    [InlineData(AppRoles.Sponsor)]
    public async Task Only_planners_and_administrators_import(string role)
    {
        using var response = await UploadAsync(TestUser.Create($"import.{role.ToLowerInvariant()}", [role]), ImportSources.PartCatalog, CatalogHeader);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Uploads_are_rate_limited()
    {
        using var browser = _host.NewBrowser();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= ImportLimits.PermitsPerMinute; i++)
        {
            using var response = await UploadAsync(Planner, ImportSources.PartCatalog, "not,a,catalogue\n", browser);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(ImportLimits.PermitsPerMinute).ShouldAllBe(s => s == HttpStatusCode.BadRequest);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private async Task<HttpResponseMessage> UploadAsync(TestUser user, string source, string csv, Browser? browser = null)
    {
        var own = browser is null ? _host.NewBrowser() : null;
        try
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent(source), "source" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", $"{_tag}.csv" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/inventory/imports", UriKind.Relative)) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
            return await (browser ?? own)!.Client.SendAsync(request, Ct);
        }
        finally
        {
            own?.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: _host.Token(Planner.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
