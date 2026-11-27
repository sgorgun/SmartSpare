using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The import screen (STAGE_5_PLAN section 6): the expected header per source, upload, the report, errors and history.</summary>
public sealed class ImportPageTests : ClientTestContext
{
    private readonly FakeImportClient _inventory = new();

    public ImportPageTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_header_of_the_chosen_source_is_shown_and_the_history_is_listed()
    {
        var page = Render<ImportsPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=import-history]").TextContent.ShouldContain("jd-catalogue.csv"));
        page.Find("[data-testid=import-columns]").TextContent.ShouldContain("brand_code,oem_part_number,description");
    }

    [Fact]
    public void A_loaded_file_shows_its_report()
    {
        var page = Render<ImportsPage>();

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("brand_code\n", "new.csv"));

        page.WaitForAssertion(() => page.Find("[data-testid=import-report]").TextContent.ShouldContain("Loaded"));
        _inventory.Uploads.ShouldHaveSingleItem().ShouldBe((ImportSources.PartCatalog, "new.csv"));
        page.FindAll("[data-testid=import-errors]").ShouldBeEmpty();
    }

    [Fact]
    public void A_file_over_the_threshold_shows_its_row_errors()
    {
        _inventory.Next = ApiResult.Success(FakeImportClient.Report(ImportStatuses.Failed,
            [new ImportRowError(3, "brand_code", "UNKNOWN_REF", "Unknown or missing brand \"XX\"")]));
        var page = Render<ImportsPage>();

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("x\n", "bad.csv"));

        page.WaitForAssertion(() => page.Find("[data-testid=import-report]").TextContent.ShouldContain("Not loaded"));
        page.Find("[data-testid=import-errors]").TextContent.ShouldContain("UNKNOWN_REF");
    }

    [Fact]
    public void A_refused_upload_shows_the_problem_with_its_reference()
    {
        _inventory.Next = ApiResult.Failure<ImportBatchDetails>(new ProblemInfo(409, "Inventory.ImportDuplicate", "Conflict",
            "This file was imported before (same content).", "ref-dup"));
        var page = Render<ImportsPage>();

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("x\n", "again.csv"));

        page.WaitForAssertion(() => page.Find("[data-testid=import-error]").TextContent
            .ShouldBe("This file was imported before (same content). Reference: ref-dup"));
    }
}

/// <summary>An <see cref="InventoryClient"/> for the import screen.</summary>
public sealed class FakeImportClient() : InventoryClient(new HttpClient())
{
    public List<(string Source, string FileName)> Uploads { get; } = [];

    public ApiResult<ImportBatchDetails>? Next { get; set; }

    public static ImportBatchDetails Report(string status, IReadOnlyList<ImportRowError> errors) => new(
        new ImportBatchResponse(58, ImportSources.PartCatalog, "new.csv", status, 3, 3 - errors.Count, errors.Count,
            status == ImportStatuses.Loaded ? 3 : 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "3 rows"),
        errors,
        errors.Count);

    public override async Task<ApiResult<ImportBatchDetails>> ImportAsync(string source, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content);
        await reader.ReadToEndAsync(cancellationToken);
        Uploads.Add((source, fileName));
        return Next ?? ApiResult.Success(Report(ImportStatuses.Loaded, []));
    }

    public override Task<ApiResult<PagedResponse<ImportBatchResponse>>> ListImportsAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<ImportBatchResponse>(
            [new ImportBatchResponse(57, ImportSources.PartCatalog, "jd-catalogue.csv", ImportStatuses.Loaded, 1200, 1196, 4, 1196, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ok")], page, pageSize, 1)));
}
