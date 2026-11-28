using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The CSV panel of a catalogue (STAGE_7_PLAN 2.6, 2.7, PR 7g): the preview is a dry run, Apply sends the same file.</summary>
public sealed class CsvPanelTests : ClientTestContext
{
    private const string Route = "api/v1/inventory/adjustment-reasons";

    private readonly FakeCsvClient _catalog = new();

    public CsvPanelTests()
    {
        Services.AddScoped<CatalogClient>(_ => _catalog);
        Services.AddLogging();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void Template_and_export_are_links_to_the_catalogue_and_import_needs_edit_rights()
    {
        var panel = Render<CsvPanel>(p => p.Add(c => c.Route, Route).Add(c => c.MayImport, false));

        panel.Find("[data-testid=csv-template]").GetAttribute("href").ShouldBe($"{Route}/csv/template");
        panel.Find("[data-testid=csv-export]").GetAttribute("href").ShouldBe($"{Route}/csv");
        panel.FindComponents<InputFile>().ShouldBeEmpty();
    }

    [Fact]
    public void A_chosen_file_is_previewed_as_a_dry_run_and_Apply_imports_the_same_file()
    {
        var imported = 0;
        var panel = Render<CsvPanel>(p => p.Add(c => c.Route, Route).Add(c => c.MayImport, true).Add(c => c.OnImported, () => imported++));

        panel.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("code,name,direction,requires_note\nA,B,IN,false\n", "reasons.csv"));

        panel.WaitForAssertion(() => panel.Find("[data-testid=csv-counts]").TextContent.ShouldBe("21 rows: 2 new, 1 changed, 17 unchanged, 1 errors."));
        panel.Find("[data-testid=csv-errors]").TextContent.ShouldContain("DUPLICATE");
        _catalog.Calls.ShouldHaveSingleItem().DryRun.ShouldBeTrue();

        panel.Find("[data-testid=csv-apply]").Click();

        panel.WaitForAssertion(() => imported.ShouldBe(1));
        _catalog.Calls.Select(c => (c.Route, c.DryRun)).ShouldBe([(Route, true), (Route, false)]);
        _catalog.Calls[1].Content.ShouldBe(_catalog.Calls[0].Content, "Apply sends the previewed file");
    }

    [Fact]
    public void Above_5_percent_errors_the_file_cannot_be_applied()
    {
        _catalog.Errors = 2;
        var panel = Render<CsvPanel>(p => p.Add(c => c.Route, Route).Add(c => c.MayImport, true));

        panel.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("x\n", "bad.csv"));

        panel.WaitForAssertion(() => panel.Find("[data-testid=csv-gate]").TextContent.ShouldContain("5 %"));
        panel.FindAll("[data-testid=csv-apply]").ShouldBeEmpty();
    }
}

/// <summary>A <see cref="CatalogClient"/> answering imports of 21 rows with <see cref="Errors"/> duplicate errors.</summary>
public sealed class FakeCsvClient() : CatalogClient(new HttpClient())
{
    public List<(string Route, bool DryRun, byte[] Content)> Calls { get; } = [];

    public int Errors { get; set; } = 1;

    public override Task<ApiResult<CatalogImportResult>> ImportCsvAsync(
        string route, string fileName, byte[] content, bool dryRun, CancellationToken cancellationToken = default)
    {
        Calls.Add((route, dryRun, content));
        var errors = Enumerable.Range(0, Errors).Select(i => new CatalogImportError(3 + i, "code", "DUPLICATE", "The code A is already on line 2.")).ToList();
        return Task.FromResult(ApiResult.Success(new CatalogImportResult(dryRun, !dryRun, 21, 2, 1, 18 - Errors, errors)));
    }
}
