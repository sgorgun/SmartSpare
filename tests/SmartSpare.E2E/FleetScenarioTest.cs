using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The stage 6 scenario (STAGE_6_PLAN section 6, UC02) against the T001 demo dataset, on a desktop, a tablet and a phone:
/// a service engineer opens a machine 8 h before its engine service, logs engine hours that cross the threshold, sees the
/// machine planned and the drafted order with its kit reserved by Inventory (an integration event), starts and completes
/// it with the engine hours, and sees it completed with the kit issued. Each viewport services its own machine, because a
/// run changes it. Screenshots go to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class FleetScenarioTest(E2EEnvironment app)
{
    private const string Engineer = "mantas.petrauskas";

    private static readonly PageAssertionsToHaveURLOptions Patient = new() { Timeout = 15_000 };

    [Theory]
    [InlineData(1280, 800, "desktop", "FT724KLP2024DEMO")]
    [InlineData(820, 1180, "tablet", "FT724KLP2024DEMO2")]
    [InlineData(390, 844, "phone", "FT724KLP2024DEMO3")]
    public async Task An_engineer_logs_hours_that_make_a_service_due_and_completes_its_order(int width, int height, string name, string vin)
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = width, Height = height },
            HasTouch = name != "desktop",
        });
        var page = await context.NewPageAsync();

        // Sign in and find the machine by part of its VIN (UC02 step 1).
        await page.GotoAsync("/fleet/machines");
        await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(Engineer);
        await page.Locator("input[autocomplete=current-password]").FillAsync(app.DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(MachinesUrl(), Patient);
        await WaitUntilInteractiveAsync(page);
        var rows = page.Locator("[data-testid=machines-grid] tbody tr.mud-table-row");
        await Assertions.Expect(rows.First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Locator("input[inputmode=search]").FillAsync(vin);
        // Exactly this VIN: FT724KLP2024DEMO is also the start of its siblings' VINs.
        var machine = rows.Filter(new() { Has = page.Locator("td").GetByText(vin, new() { Exact = true }) });
        await Assertions.Expect(machine).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await machine.GetByRole(AriaRole.Link, new() { NameRegex = OpenLink() }).ClickAsync();

        // The card: 8 h before the 750 h engine service, due soon.
        await Assertions.Expect(page.Locator("[data-testid=machine-vin]")).ToHaveTextAsync(vin, new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=machine-due]")).ToContainTextAsync("Due soon");
        await Assertions.Expect(page.Locator("[data-testid=machine-plans]")).ToContainTextAsync("Next service at 750 h · 8 h to go");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"fleet-1-card-{name}.png"), FullPage = true });

        // Log 751 h: the dialog opens the decimal keypad, the reading drafts the engine service (UC02 steps 2–7).
        await page.Locator("[data-testid=log-hours]").ClickAsync();
        var hours = page.GetByLabel("Engine hours", new() { Exact = true });
        await Assertions.Expect(hours).ToHaveAttributeAsync("inputmode", "decimal");
        await hours.FillAsync("751");
        await page.Locator("[data-testid=reading-save]").ClickAsync();
        var drafted = page.Locator("[data-testid=reading-drafted]");
        await Assertions.Expect(drafted).ToContainTextAsync("for the service at 750 h", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=machine-due]")).ToContainTextAsync("Order planned", new() { Timeout = 15_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"fleet-2-reading-{name}.png") });
        await drafted.Locator("a").First.ClickAsync();

        // The drafted order: Inventory reserves the kit through ServiceOrderDrafted → ServiceKitReserved; the page follows it.
        await Assertions.Expect(page).ToHaveURLAsync(OrderUrl(), Patient);
        await Assertions.Expect(page.Locator("[data-testid=order-kit]")).ToContainTextAsync("Kit reserved", new() { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("[data-testid=order-status]")).ToContainTextAsync("Parts reserved");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"fleet-3-order-reserved-{name}.png"), FullPage = true });

        // Start, then complete with the engine hours (prefilled with the machine's 751 h).
        await page.Locator("[data-testid=action-start]").ClickAsync();
        await page.Locator("[data-testid=confirm]").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=order-status]")).ToContainTextAsync("In progress", new() { Timeout = 15_000 });
        await page.Locator("[data-testid=action-complete]").ClickAsync();
        var completionHours = page.GetByLabel("Engine hours at completion");
        await Assertions.Expect(completionHours).ToHaveValueAsync("751", new() { Timeout = 15_000 });
        await page.Locator("[data-testid=confirm]").ClickAsync();

        // Completed; Inventory issues the kit (ServiceOrderCompleted → ServiceKitIssued) and nothing is missing.
        await Assertions.Expect(page.Locator("[data-testid=order-status]")).ToContainTextAsync("Completed", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=order-kit]")).ToContainTextAsync("Kit issued");
        await Assertions.Expect(page.Locator("[data-testid=order-lines]")).ToContainTextAsync("18 issued", new() { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("[data-testid=order-lines]")).Not.ToContainTextAsync("short");
        await Assertions.Expect(page.Locator("[data-testid=order-actions]")).ToHaveCountAsync(0);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact($"fleet-4-order-completed-{name}.png"), FullPage = true });

        // Nothing on the screen ran off its edge (the tabs, the tables and the action bar fit the viewport).
        var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.ShouldBeLessThanOrEqualTo(0, $"the page scrolls sideways at {width} px");
    }

    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    [GeneratedRegex("/fleet/machines$")]
    private static partial Regex MachinesUrl();

    [GeneratedRegex(@"/fleet/service-orders/\d+$")]
    private static partial Regex OrderUrl();

    [GeneratedRegex("^Open ")]
    private static partial Regex OpenLink();
}
