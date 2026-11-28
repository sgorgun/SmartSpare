using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The import screen against the T001 demo dataset (STAGE_5_PLAN section 6): the inventory planner uploads a small part
/// catalogue, sees the load report and the batch in the history, and the new part in the catalogue.
/// Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class ImportScreenTest(E2EEnvironment app)
{
    private const string Planner = "ignas.zukauskas";

    [Fact]
    public async Task A_planner_imports_a_part_catalogue_and_finds_the_new_part()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/inventory/imports");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(Planner);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(ImportsUrl());
        await WaitUntilInteractiveAsync(page);

        var part = "E2E-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var csv = "brand_code,oem_part_number,description,category_code,uom_code,weight_kg,unit_cost,condition_based\n"
            + $"CLAAS,{part},Imported through the screen,OIL_FILTER,PCS,0.3,12.50,no\n";
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = $"{part}.csv",
            MimeType = "text/csv",
            Buffer = Encoding.UTF8.GetBytes(csv),
        });

        await Assertions.Expect(page.Locator("[data-testid=import-report]")).ToContainTextAsync("Loaded", new() { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("[data-testid=import-history]")).ToContainTextAsync($"{part}.csv");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Choose CSV file" })).ToBeEnabledAsync();
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("import-1-report.png"), FullPage = true });

        await page.GetByRole(AriaRole.Link, new() { Name = "Parts" }).ClickAsync();
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[inputmode=search]").FillAsync(part);
        var rows = page.Locator("[data-testid=catalog-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Assertions.Expect(rows.First).ToContainTextAsync(part);
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/inventory/imports$")]
    private static partial Regex ImportsUrl();
}
