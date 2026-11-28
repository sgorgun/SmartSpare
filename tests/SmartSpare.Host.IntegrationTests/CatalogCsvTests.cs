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
/// CSV for every catalogue (STAGE_7_PLAN 2.6, PR 7g), tested on the adjustment reasons: the template, an export that
/// imports back unchanged, upsert by code, DUPLICATE and ARCHIVED errors, the 5 % gate, and a dry run whose answer is
/// the commit's answer while it keeps nothing.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class CatalogCsvTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Reasons = "/api/v1/inventory/adjustment-reasons";
    private const string Header = "code,name,direction,requires_note";

    private readonly string _tag = "CV" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private TestUser _planner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var id = await database.CreateUserAsync($"csv.planner.{_tag.ToLowerInvariant()}", [AppRoles.InventoryPlanner], []);
        _planner = new TestUser(id, "csv.planner", "unused", [AppRoles.InventoryPlanner], []);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_template_has_the_header_and_an_example_and_the_export_imports_back_unchanged()
    {
        var template = await TextAsync($"{Reasons}/csv/template");
        template.TrimStart((char)0xFEFF).Split("\r\n")[0].ShouldBe(Header);

        await ImportAsync($"{Header}\n{_tag}_A,Torn packaging,out,false\n", dryRun: false);
        var export = await TextAsync($"{Reasons}/csv");
        export.ShouldContain($"{_tag}_A,Torn packaging,OUT,false");

        var again = await ImportAsync(export, dryRun: true);
        (again.Created, again.Updated, again.Errors.Count).ShouldBe((0, 0, 0), "an export imports back without a change");
        again.Unchanged.ShouldBe(again.Rows);
    }

    [Fact]
    public async Task A_dry_run_answers_exactly_what_the_import_does_and_keeps_nothing()
    {
        await ImportAsync($"{Header}\n{_tag}_SAME,Same,in,false\n{_tag}_EDIT,Before,in,false\n", dryRun: false);
        var file = $"{Header}\n{_tag}_SAME,Same,in,false\n{_tag}_EDIT,After,both,true\n{_tag}_NEW1,New one,out,false\n{_tag}_NEW2,New two,out,true\n";

        var preview = await ImportAsync(file, dryRun: true);
        (preview.DryRun, preview.Applied, preview.Rows, preview.Created, preview.Updated, preview.Unchanged).ShouldBe((true, false, 4, 2, 1, 1));
        (await SearchAsync()).Select(r => r.Code).ShouldBe([$"{_tag}_EDIT", $"{_tag}_SAME"], "the dry run was rolled back");

        var done = await ImportAsync(file, dryRun: false);
        (done.DryRun, done.Applied).ShouldBe((false, true));
        (done.Rows, done.Created, done.Updated, done.Unchanged, done.Errors.Count)
            .ShouldBe((preview.Rows, preview.Created, preview.Updated, preview.Unchanged, preview.Errors.Count), "the preview is the commit");
        var stored = await SearchAsync();
        stored.Count.ShouldBe(4);
        stored.Single(r => r.Code == $"{_tag}_EDIT").ShouldSatisfyAllConditions(
            r => r.Name.ShouldBe("After"), r => r.Direction.ShouldBe(AdjustmentDirections.Both), r => r.RequiresNote.ShouldBeTrue());
    }

    [Fact]
    public async Task Duplicates_and_archived_codes_are_row_errors_and_above_5_percent_nothing_is_written()
    {
        await ImportAsync($"{Header}\n{_tag}_OLD,Old,out,false\n", dryRun: false);
        var old = (await SearchAsync()).Single();
        using (var browser = _host.NewBrowser())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Reasons}/{old.Id}/archive", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(_planner));
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{old.Version}\"");
            using var archived = await browser.Client.SendAsync(request, Ct);
            archived.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var bad = await ImportAsync($"{Header}\n{_tag}_X,X,out,false\n{_tag}_X,X again,out,false\n{_tag}_OLD,Old,out,false\n{_tag}_Y,Y,sideways,maybe\n", dryRun: false);
        bad.Applied.ShouldBeFalse("3 of 4 rows have errors");
        bad.Errors.Select(e => (e.Line, e.Code)).ShouldBe([(3, "DUPLICATE"), (4, "ARCHIVED"), (5, "INVALID")]);
        (await SearchAsync()).ShouldBeEmpty("nothing was written; the archived reason is not among the active ones");

        // 1 error in 21 rows is under 5 %: the valid rows are written, the refused one is reported.
        var rows = string.Concat(Enumerable.Range(1, 20).Select(i => $"{_tag}_R{i:00},Reason {i},in,false\n"));
        var mostly = await ImportAsync($"{Header}\n{rows}{_tag}_R01,Twice,in,false\n", dryRun: false);
        (mostly.Applied, mostly.Created, mostly.Errors.Single().Code).ShouldBe((true, 20, "DUPLICATE"));
        (await SearchAsync()).Count.ShouldBe(20);
    }

    [Fact]
    public async Task Importing_needs_the_create_and_edit_permissions_and_a_header_error_names_the_columns()
    {
        var engineerId = await database.CreateUserAsync($"csv.engineer.{_tag.ToLowerInvariant()}", [AppRoles.ServiceEngineer], []);
        var engineer = new TestUser(engineerId, "csv.engineer", "unused", [AppRoles.ServiceEngineer], []);
        using (var refused = await PostFileAsync($"{Header}\n{_tag}_Z,Z,in,false\n", false, engineer))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var header = await PostFileAsync("code,name\nA,B\n", false, _planner);
        header.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Problem.ReadAsync(header);
        problem.Code.ShouldBe("MasterData.CsvHeader");
        problem.Detail!.ShouldContain("Missing: direction, requires_note");
    }

    private async Task<CatalogImportResult> ImportAsync(string csv, bool dryRun)
    {
        using var response = await PostFileAsync(csv, dryRun, _planner);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CatalogImportResult>(Ct))!;
    }

    private async Task<HttpResponseMessage> PostFileAsync(string csv, bool dryRun, TestUser user)
    {
        using var browser = _host.NewBrowser();
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "reasons.csv" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Reasons}/csv?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<List<AdjustmentReasonDto>> SearchAsync()
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"{Reasons}?search={_tag}&pageSize=100", bearer: Token(_planner));
        return [.. (await response.Content.ReadFromJsonAsync<PagedResponse<AdjustmentReasonDto>>(Ct))!.Items];
    }

    private async Task<string> TextAsync(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(_planner));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private string Token(TestUser user) => _host.Token(user.Principal());
}
