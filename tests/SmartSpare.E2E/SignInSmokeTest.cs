using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The stage 3 smoke test in a real browser (STAGE_3_PLAN 9.4): redirect to sign-in, the generic failure message with its
/// trace id, sign-in as a brand manager with the role-based menu, and sign-out. Screenshots of every step and a Playwright
/// trace are written to TestResults/e2e.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed partial class SignInSmokeTest(E2EEnvironment app)
{
    private const string BrandManager = "tomas.radzevicius";

    [Fact]
    public async Task A_demo_user_signs_in_sees_the_brand_manager_menu_and_signs_out()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,       // throw-away certificate of the test host
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
        var page = await context.NewPageAsync();
        var passed = false;
        try
        {
            // 1. Anonymous: the home page sends the user to sign-in.
            await page.GotoAsync("/");
            await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
            await WaitUntilInteractiveAsync(page);
            await Shot(page, "1-login");

            // 2. A wrong password: the generic message with the W3C trace id as reference (#60, #72).
            await SignInAsync(page, BrandManager, "not the password");
            var alert = page.GetByRole(AriaRole.Alert);
            await Assertions.Expect(alert).ToContainTextAsync("The user name or password is incorrect.");
            await Assertions.Expect(alert).ToContainTextAsync(Reference());
            await page.Locator("[data-interactive]").ScreenshotAsync(new LocatorScreenshotOptions { Path = Artifact("login-error.png") });

            // 3. The right password: the home page with the brand manager's menu.
            await SignInAsync(page, BrandManager, app.DemoPassword);
            await Assertions.Expect(page).ToHaveURLAsync(new Uri(app.BaseAddress, "/").ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync($"Welcome, {BrandManager}");
            var menu = page.Locator(".mud-nav-link");
            foreach (var area in new[] { "Home", "Inventory", "Fleet", "Procurement", "Forecasting", "Reporting" })
            {
                await Assertions.Expect(menu.Filter(new() { HasText = area })).ToHaveCountAsync(1);
            }

            await Shot(page, "3-home");

            // Nothing token-like reaches the browser's scripts (exit criterion 1, ADR 0014): the session cookie is
            // HttpOnly, and neither document.cookie nor web storage holds it or anything named like a credential.
            var session = (await context.CookiesAsync()).Single(c => c.Name == "__Host-SmartSpare");
            session.HttpOnly.ShouldBeTrue();
            session.Secure.ShouldBeTrue();
            var scriptVisible = await page.EvaluateAsync<string>(
                "() => JSON.stringify({ cookie: document.cookie, local: { ...localStorage }, session: { ...sessionStorage } })");
            scriptVisible.ShouldNotContain(session.Value);
            CredentialName().IsMatch(scriptVisible).ShouldBeFalse(scriptVisible);

            // 4. Sign out: back to sign-in, and the session is gone. The page was reloaded after sign-in, so wait until
            //    WebAssembly runs it again; a click on the prerendered menu would do nothing.
            await WaitUntilInteractiveAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = $"Account of {BrandManager}" }).ClickAsync();
            await page.GetByText("Sign out").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(LoginUrl());
            using (var me = JsonDocument.Parse(await (await context.APIRequest.GetAsync("/bff/me")).TextAsync()))
            {
                me.RootElement.GetProperty("isAuthenticated").GetBoolean().ShouldBeFalse();
            }

            await Shot(page, "4-signed-out");
            passed = true;
        }
        finally
        {
            // The trace is kept for every failed run; CI uploads TestResults/e2e when the job fails.
            await context.Tracing.StopAsync(new TracingStopOptions { Path = passed ? null : Artifact("trace.zip") });
            if (!passed)
            {
                await Shot(page, "failure");
            }
        }
    }

    /// <summary>Waits until WebAssembly runs the page (the sign-in form and the main layout mark themselves), so clicks reach it.</summary>
    private static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator("[data-interactive=true]")).ToBeVisibleAsync(new() { Timeout = 60_000 });

    private static async Task SignInAsync(IPage page, string userName, string password)
    {
        await WaitUntilInteractiveAsync(page);
        await page.Locator("input[autocomplete=username]").FillAsync(userName);
        await page.Locator("input[autocomplete=current-password]").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    }

    private async Task Shot(IPage page, string name) =>
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifact(name + ".png"), FullPage = true });

    private string Artifact(string name) => Path.Combine(app.ArtifactsDirectory, name);

    [GeneratedRegex("/login")]
    private static partial Regex LoginUrl();

    /// <summary>Names a stored credential would have (Blazor caches only its resource hash in localStorage).</summary>
    [GeneratedRegex("token|bearer|jwt|__Host-|password|xsrf|antiforgery", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();

    /// <summary>"Reference: " followed by a W3C trace id (32 lower-case hex digits).</summary>
    [GeneratedRegex("Reference: [0-9a-f]{32}")]
    private static partial Regex Reference();
}
