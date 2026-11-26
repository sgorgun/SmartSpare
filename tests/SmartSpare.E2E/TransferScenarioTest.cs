using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The stage 5 scenario (STAGE_5_PLAN section 6) against the T001 demo dataset: the inventory planner proposes a transfer
/// of the UC01 part from Klaipėda to Panevėžys, approves it, and sees the firm reservation at the donor; on a tablet the
/// transfers list shows the order as a card. Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class TransferScenarioTest(E2EEnvironment app)
{
    private const string Planner = "ignas.zukauskas";

    [Fact]
    public async Task A_planner_proposes_and_approves_a_transfer_and_the_donor_holds_the_stock()
    {
        var page = await SignInAsync(1280, 800);

        await page.Locator("[data-testid=new-transfer]").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(NewUrl());

        await ChooseAsync(page, "[data-testid=from-hub]", "KLP ·");
        await ChooseAsync(page, "[data-testid=to-hub]", "PNV ·");
        await page.Locator("input[data-testid=line-part-0], [data-testid=line-part-0] input").FillAsync("CP-4821");
        await page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasText = "CP-4821" }).First.ClickAsync();
        await page.Locator("input[data-testid=line-quantity-0], [data-testid=line-quantity-0] input").FillAsync("2");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("transfer-1-new.png"), FullPage = true });
        await page.Locator("[data-testid=propose]").ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(DetailUrl());
        await Assertions.Expect(page.Locator("[data-testid=transfer-status]")).ToContainTextAsync("Proposed", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=transfer-lines]")).ToContainTextAsync("CP-4821");

        await page.Locator("[data-testid=action-approve]").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=confirm-text]")).ToContainTextAsync("firmly reserved");
        await page.Locator("[data-testid=confirm]").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=transfer-status]")).ToContainTextAsync("Approved", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=transfer-actions]")).ToHaveCountAsync(1, new() { Timeout = 5_000 });
        await Assertions.Expect(page.Locator("[data-testid=action-approve]")).ToHaveCountAsync(0);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("transfer-2-approved.png"), FullPage = true });
        var number = (await page.GetByRole(AriaRole.Heading, new() { Level = 2 }).TextContentAsync())!.Trim();

        // The donor position shows the firm reservation of the new order.
        await page.GotoAsync("/inventory/stock");
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[inputmode=search]").FillAsync("cp-4821");
        var rows = page.Locator("[data-testid=stock-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows).ToHaveCountAsync(7, new() { Timeout = 15_000 });   // CP-4821 is stocked in the seven hubs
        var klp = rows.Filter(new() { HasText = "KLP" }).Filter(new() { HasText = "CP-4821" });
        await klp.First.GetByRole(AriaRole.Link, new() { NameRegex = OpenLink() }).ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=reservations]")).ToContainTextAsync(number, new() { Timeout = 15_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("transfer-3-donor-reservation.png"), FullPage = true });
    }

    [Fact]
    public async Task On_a_tablet_the_transfers_list_shows_orders_as_cards()
    {
        var page = await SignInAsync(600, 960);

        var rows = page.Locator("[data-testid=transfers-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(rows.First).ToContainTextAsync("TR-");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact("transfer-4-list-tablet.png"), FullPage = true });
    }

    private async Task<IPage> SignInAsync(int width, int height)
    {
        var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/inventory/transfers");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(Planner);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(TransfersUrl());
        await WaitUntilInteractiveAsync(page);
        return page;
    }

    private static async Task ChooseAsync(IPage page, string select, string option)
    {
        await page.Locator(select).ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasText = option }).First.ClickAsync();
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/inventory/transfers$")]
    private static partial Regex TransfersUrl();

    [GeneratedRegex("/inventory/transfers/new$")]
    private static partial Regex NewUrl();

    [GeneratedRegex(@"/inventory/transfers/\d+$")]
    private static partial Regex DetailUrl();

    [GeneratedRegex("^Open ")]
    private static partial Regex OpenLink();
}
