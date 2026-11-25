using Microsoft.Playwright;

namespace SmartSpare.E2E;

/// <summary>
/// The Scalar API reference in a real browser (ADR 0019, STAGE_4_PLAN section 7): it renders anonymously under its nonce
/// CSP, the browser reports no CSP violation, and nothing is loaded from another origin (no CDN fonts).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2EGroup.Name)]
public sealed class ApiReferenceTest(E2EEnvironment app)
{
    [Fact]
    public async Task The_api_reference_renders_under_its_nonce_policy_without_external_requests()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = app.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        var page = await context.NewPageAsync();
        var violations = new List<string>();
        var foreign = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(message.Text);
            }
        };
        page.Request += (_, request) =>
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal)
                && url.Authority != app.BaseAddress.Authority)
            {
                foreign.Add(request.Url);
            }
        };

        var response = await page.GotoAsync("/scalar");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        response!.Status.ShouldBe(200);
        (await response.HeaderValueAsync("content-security-policy")).ShouldNotBeNull().ShouldContain("'nonce-");
        await Assertions.Expect(page.GetByText("Identity", new() { Exact = false }).First).ToBeVisibleAsync();
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(app.ArtifactsDirectory, "api-reference.png") });
        violations.ShouldBeEmpty();
        foreign.ShouldBeEmpty();
    }
}
