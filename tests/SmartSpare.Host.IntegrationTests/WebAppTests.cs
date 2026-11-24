using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The Blazor Web App served by the host (STAGE_3_PLAN 8.1, 8.7, 1.4): security headers, the CSP hash of the inline
/// import map, sign-in redirects, the prerendered role-based menu and the allow-listed principal.
/// </summary>
public sealed partial class WebAppTests(FakeIdentityHostFactory factory) : IClassFixture<FakeIdentityHostFactory>
{
    [Fact]
    public async Task Pages_carry_the_security_headers_with_a_strict_script_policy()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/login");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var csp = Csp(response);
        var directives = Directives(csp);
        directives["default-src"].ShouldBe(["'self'"]);
        directives["script-src"].ShouldContain("'self'");
        directives["script-src"].ShouldContain("'wasm-unsafe-eval'");
        directives["script-src"].ShouldNotContain("'unsafe-inline'");
        directives["script-src"].ShouldNotContain("'unsafe-eval'");
        directives["script-src"].ShouldContain(s => s.StartsWith("'sha256-", StringComparison.Ordinal));
        directives["style-src"].ShouldBe(["'self'", "'unsafe-inline'"]);
        directives["img-src"].ShouldBe(["'self'", "data:"]);
        directives["connect-src"].ShouldBe(["'self'"]);
        directives["object-src"].ShouldBe(["'none'"]);
        directives["base-uri"].ShouldBe(["'self'"]);
        directives["frame-ancestors"].ShouldBe(["'none'"]);
        directives.ShouldContainKey("upgrade-insecure-requests");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["strict-origin-when-cross-origin"]);
    }

    [Fact]
    public async Task The_csp_hash_matches_the_inline_import_map_of_the_page()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var importMap = ImportMap().Match(html);
        importMap.Success.ShouldBeTrue("the page renders an inline import map");
        // Hash it as a browser does: HTML parsing turns CRLF into LF before the inline script is hashed.
        var script = importMap.Groups["json"].Value.ReplaceLineEndings("\n");
        var hash = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";
        Directives(Csp(response))["script-src"].ShouldContain(hash);
        InlineScripts().Count(html).ShouldBe(1, "the import map is the only inline script; everything else loads from 'self'");
    }

    [Fact]
    public async Task Api_responses_carry_the_headers_without_an_import_map_hash()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/health/live");

        Directives(Csp(response))["script-src"].ShouldBe(["'self'", "'wasm-unsafe-eval'"]);
    }

    [Fact]
    public async Task An_anonymous_page_request_is_sent_to_sign_in_with_its_return_url()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/inventory?tab=stock");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.ShouldBe("/login?ReturnUrl=%2Finventory%3Ftab%3Dstock");
        Csp(response).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Everything_webassembly_loads_on_the_sign_in_page_is_public()
    {
        // WebAssembly must start for an anonymous user on /login: every module the import map names, including the
        // resource collection (a component endpoint, not a static asset), answers 200 with JavaScript, not the redirect.
        using var browser = factory.NewBrowser();
        using var page = await browser.GetAsync("/login");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var targets = ImportTargets().Matches(ImportMap().Match(html).Groups["json"].Value)
            .Select(m => m.Groups["url"].Value.TrimStart('.'))
            .Distinct()
            .ToList();
        targets.ShouldContain(u => u.Contains("resource-collection", StringComparison.Ordinal));

        foreach (var url in targets.Append("/_framework/blazor.web.js"))
        {
            using var response = await browser.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            response.Content.Headers.ContentType?.MediaType.ShouldBeOneOf(["text/javascript", "application/javascript"], url);
        }
    }

    [Fact]
    public async Task The_prerendered_menu_follows_the_roles_and_the_page_carries_no_security_stamp()
    {
        var keeper = factory.Users.Add(TestUser.Create("lukas.keeper", [AppRoles.WarehouseKeeper], [2], warehouse: 4));
        using var browser = factory.NewBrowser();
        await browser.LoginOrFailAsync(keeper);

        using var response = await browser.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Welcome, lukas.keeper");
        html.ShouldContain("href=\"inventory\"", customMessage: "a warehouse keeper moves stock");
        html.ShouldContain("href=\"procurement\"", customMessage: "a warehouse keeper receives purchase orders");
        html.ShouldNotContain("href=\"forecasting\"", customMessage: "only brand managers and planners decide recommendations");
        html.ShouldNotContain("href=\"fleet\"");

        // The principal handed to WebAssembly: allow-listed claims only (STAGE_3_PLAN 1.4).
        var state = PersistedState(html);
        state.ShouldContain(AppClaimTypes.Role);
        state.ShouldContain(AppRoles.WarehouseKeeper);
        state.ShouldContain(AppClaimTypes.Warehouse);
        state.ShouldNotContain(IdentityClaimTypes.SecurityStamp);
        state.ShouldNotContain(keeper.Stamp);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task Hsts_is_sent_outside_development(string environment, bool expected)
    {
        await using var host = new HostFactory { EnvironmentName = environment };
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://smartspare.example"), AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.Contains("Strict-Transport-Security").ShouldBe(expected);
    }

    private static string Csp(HttpResponseMessage response) => string.Join(", ", response.Headers.GetValues("Content-Security-Policy"));

    private static Dictionary<string, string[]> Directives(string csp) =>
        csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(d => d[0], d => d[1..], StringComparer.Ordinal);

    /// <summary>The persisted component state of the prerendered page, decoded (it carries the serialized principal).</summary>
    private static string PersistedState(string html)
    {
        var match = PersistedStateComment().Match(html);
        match.Success.ShouldBeTrue("the prerendered page persists its state for WebAssembly");
        var outer = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["state"].Value));
        // Each persisted value is base64 of its JSON inside the outer JSON document.
        var decoded = Base64Value().Matches(outer)
            .Select(m => { try { return Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups["v"].Value)); } catch (FormatException) { return string.Empty; } });
        return outer + string.Join('\n', decoded);
    }

    [GeneratedRegex("<script type=\"importmap\"[^>]*>(?<json>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ImportMap();

    /// <summary>The fingerprinted module URLs the import map points to (its values).</summary>
    [GeneratedRegex(": \"(?<url>\\./_framework/[^\"]+\\.js)\"")]
    private static partial Regex ImportTargets();

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.Singleline)]
    private static partial Regex InlineScripts();

    [GeneratedRegex("<!--Blazor-WebAssembly-Component-State:(?<state>[A-Za-z0-9+/=]+)-->")]
    private static partial Regex PersistedStateComment();

    [GeneratedRegex("\"(?<v>[A-Za-z0-9+/]{16,}={0,2})\"")]
    private static partial Regex Base64Value();
}
