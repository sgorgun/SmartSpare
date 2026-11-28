using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Manufacturers (STAGE_7_PLAN 3.1, PR 7h3): the catalogue, its SS501 guard on active parts, and the optional
/// manufacturer_code column of the parts import (unknown or archived is a row error; empty keeps the stored maker; old
/// files without the column still import). Single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ManufacturerTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Manufacturers = "/api/v1/inventory/manufacturers";
    private const string Header = "brand_code,oem_part_number,description,category_code,uom_code,weight_kg,unit_cost,condition_based";

    private static readonly TestUser Planner = TestUser.Create("maker.planner", [AppRoles.InventoryPlanner]);

    private readonly string _tag = "MF" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_parts_import_sets_a_manufacturer_from_the_optional_column_and_an_empty_one_keeps_it()
    {
        var maker = await CreateAsync();

        var first = await ImportAsync($"{Header},manufacturer_code\nJD,{_tag}-1,Filter made by a maker,OIL_FILTER,PCS,,10,no,{maker.Code.ToLowerInvariant()}\n");
        first.Batch.Status.ShouldBe(ImportStatuses.Loaded);
        (await MakerOfAsync($"{_tag}-1")).ShouldBe(maker.Id);

        // A file of the old shape (no column) and a file with an empty value change the description but keep the maker.
        (await ImportAsync($"{Header}\nJD,{_tag}-1,Renamed once,OIL_FILTER,PCS,,10,no\n")).Batch.Status.ShouldBe(ImportStatuses.Loaded);
        (await ImportAsync($"{Header},manufacturer_code\nJD,{_tag}-1,Renamed twice,OIL_FILTER,PCS,,10,no,\n")).Batch.Status.ShouldBe(ImportStatuses.Loaded);
        (await MakerOfAsync($"{_tag}-1")).ShouldBe(maker.Id);
    }

    [Fact]
    public async Task An_unknown_or_archived_manufacturer_is_a_row_error()
    {
        var archived = await CreateAsync();
        await database.ExecuteAsync("UPDATE catalog.manufacturer SET is_archived = true, archived_at = now() WHERE id = @id", ("id", archived.Id));

        var result = await ImportAsync($"{Header},manufacturer_code\nJD,{_tag}-2,Unknown maker,OIL_FILTER,PCS,,10,no,NOBODY\nJD,{_tag}-3,Archived maker,OIL_FILTER,PCS,,10,no,{archived.Code}\n");

        result.Batch.Status.ShouldBe(ImportStatuses.Failed);
        result.Errors.Select(e => (e.LineNo, e.Column, e.Code)).ShouldBe([(2, "manufacturer_code", "UNKNOWN_REF"), (3, "manufacturer_code", "UNKNOWN_REF")]);
    }

    [Fact]
    public async Task A_manufacturer_with_active_parts_cannot_be_archived_and_the_export_carries_its_code()
    {
        var maker = await CreateAsync();
        (await ImportAsync($"{Header},manufacturer_code\nJD,{_tag}-4,Exported part,OIL_FILTER,PCS,,10,no,{maker.Code}\n")).Batch.Status.ShouldBe(ImportStatuses.Loaded);

        using (var refused = await SendAsync(HttpMethod.Post, $"{Manufacturers}/{maker.Id}/archive", null, maker.Version))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
            body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldBe(["1 active part(s)"]);
        }

        using var export = await SendAsync(HttpMethod.Get, $"/api/v1/inventory/imports/export?source={ImportSources.PartCatalog}");
        var text = await export.Content.ReadAsStringAsync(Ct);
        text.TrimStart((char)0xFEFF).ShouldStartWith(Header + ",manufacturer_code");
        text.ShouldContain($"JD,{_tag}-4,Exported part,OIL_FILTER,PCS,,10.00,false,{maker.Code}");
    }

    private async Task<ManufacturerDto> CreateAsync()
    {
        var code = _tag + Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        using var created = await SendAsync(HttpMethod.Post, Manufacturers, new ManufacturerWrite(code, $"Maker {code}", "DE", "https://maker.example"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<ManufacturerDto>(Ct))!;
    }

    private Task<short?> MakerOfAsync(string oem) =>
        database.ScalarAsync<short?>("SELECT manufacturer_id FROM catalog.part WHERE oem_part_number = @oem", ("oem", oem));

    private async Task<ImportBatchDetails> ImportAsync(string csv)
    {
        using var browser = _host.NewBrowser();
        using var content = new MultipartFormDataContent
        {
            { new StringContent(ImportSources.PartCatalog), "source" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", $"{Guid.NewGuid():N}.csv" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/inventory/imports", UriKind.Relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Planner.Principal()));
        using var response = await browser.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ImportBatchDetails>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? version = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Planner.Principal()));
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }
}
