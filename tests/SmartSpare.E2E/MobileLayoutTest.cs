using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The web app on a phone (NFR05, ADR 0006): at 390 × 844 the sign-in page and the main layout fit the screen without a
/// horizontal scroll bar, and the menu opens from the app bar and leads to an area. Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class MobileLayoutTest(E2EEnvironment app)
{
    private const string BrandManager = "tomas.radzevicius";

    [Fact]
    public async Task On_a_phone_the_pages_fit_the_screen_and_the_menu_is_reachable()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            DeviceScaleFactor = 3,
            IsMobile = true,
            HasTouch = true,
        });
        var page = await context.NewPageAsync();

        // The sign-in page.
        await page.GotoAsync("/");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await ShouldFitTheScreenAsync(page, "sign-in page");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("mobile-1-login.png"), FullPage = true });

        // The main layout after sign-in.
        await page.Locator("input[autocomplete=username]").FillAsync(BrandManager);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync($"Welcome, {BrandManager}");
        await WaitUntilInteractiveAsync(page);
        await ShouldFitTheScreenAsync(page, "home page");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("mobile-2-home.png"), FullPage = true });

        // The menu: opened from the app bar if it is closed, then an area is one tap away.
        var inventory = page.Locator(".mud-nav-link").Filter(new() { HasText = "Inventory" });
        if (!await inventory.IsVisibleAsync())
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Menu" }).ClickAsync();
        }

        await Assertions.Expect(inventory).ToBeVisibleAsync();
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("mobile-3-menu.png") });
        await inventory.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(InventoryUrl());
        await ShouldFitTheScreenAsync(page, "inventory page");
    }

    /// <summary>No horizontal scrolling: the document is no wider than the viewport.</summary>
    private static async Task ShouldFitTheScreenAsync(IPage page, string what)
    {
        var widths = await page.EvaluateAsync<int[]>(
            "() => [document.documentElement.scrollWidth, document.documentElement.clientWidth, document.body.scrollWidth]");
        widths[0].ShouldBeLessThanOrEqualTo(widths[1], $"{what}: document {widths[0]} px wide in a {widths[1]} px viewport");
        widths[2].ShouldBeLessThanOrEqualTo(widths[1], $"{what}: body {widths[2]} px wide in a {widths[1]} px viewport");
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/inventory$")]
    private static partial Regex InventoryUrl();
}
