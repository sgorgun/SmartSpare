using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The parts catalogue on the Inventory page against the T001 demo dataset: the first page from the server, a search
/// for the UC01 part, and the next page. Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class PartsCatalogueTest(E2EEnvironment app)
{
    private const string BrandManager = "tomas.radzevicius";

    [Fact]
    public async Task A_signed_in_user_pages_and_searches_the_parts_catalogue()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/inventory");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(BrandManager);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(InventoryUrl());
        await WaitUntilInteractiveAsync(page);

        // The first page: 25 rows of the demo catalogue and a pager with the total.
        var rows = page.Locator("[data-testid=catalog-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows).ToHaveCountAsync(25, new() { Timeout = 30_000 });
        var firstPart = await rows.First.Locator("td").First.TextContentAsync();
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("parts-1-first-page.png"), FullPage = true });

        // The next page comes from the server too.
        await page.GetByRole(AriaRole.Button, new() { Name = "Next page" }).ClickAsync();
        await Assertions.Expect(rows.First.Locator("td").First).Not.ToHaveTextAsync(firstPart!);

        // A search for the UC01 part goes back to page 1 and finds exactly it.
        await page.Locator("input[inputmode=search]").FillAsync("cp-4821");
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await Assertions.Expect(rows.First).ToContainTextAsync("CP-4821");
        await Assertions.Expect(rows.First).ToContainTextAsync("Heavy-duty straw chopper blade");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("parts-2-search.png"), FullPage = true });

        await Assertions.Expect(page.Locator("[data-testid=catalog-error]")).ToHaveCountAsync(0);
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/inventory$")]
    private static partial Regex InventoryUrl();
}
