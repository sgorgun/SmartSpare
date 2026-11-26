using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The stock screens against the T001 demo dataset (STAGE_5_PLAN section 6): the overview from the server, a search for
/// the UC01 part, its position page, and the same overview at phone width, where rows become cards.
/// Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class StockScreensTest(E2EEnvironment app)
{
    private const string BrandManager = "tomas.radzevicius";

    [Theory]
    [InlineData(1280, 800, "desktop")]
    [InlineData(390, 844, "phone")]
    public async Task A_user_finds_a_part_in_the_stock_overview_and_opens_its_position(int width, int height, string name)
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/inventory/stock");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(BrandManager);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(StockUrl());
        await WaitUntilInteractiveAsync(page);

        var rows = page.Locator("[data-testid=stock-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.Locator("input[inputmode=search]").FillAsync("cp-4821");
        await Assertions.Expect(rows.First).ToContainTextAsync("CP-4821", new() { Timeout = 15_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"stock-1-overview-{name}.png"), FullPage = true });

        await rows.First.GetByRole(AriaRole.Link, new() { NameRegex = OpenLink() }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(PositionUrl());
        await Assertions.Expect(page.Locator("[data-testid=position-figures]")).ToContainTextAsync("Available", new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 2 })).ToContainTextAsync("CP-4821");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"stock-2-position-{name}.png"), FullPage = true });

        await Assertions.Expect(page.Locator("[data-testid=position-error]")).ToHaveCountAsync(0);
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/inventory/stock$")]
    private static partial Regex StockUrl();

    [GeneratedRegex(@"/inventory/stock/\d+/\d+$")]
    private static partial Regex PositionUrl();

    [GeneratedRegex("^Open ")]
    private static partial Regex OpenLink();
}
