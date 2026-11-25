using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// API documentation in the single host (ADR 0019, STAGE_4_PLAN section 7): one public document per module with only its
/// versioned business API, and the Scalar reference under its own nonce policy.
/// </summary>
public sealed partial class ApiDocumentationTests : IAsyncLifetime
{
    private readonly FakeIdentityHostFactory _host = new();

    public static TheoryData<string> Modules => ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Each_module_has_a_public_document_with_only_its_versioned_api(string module)
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync(ApiDocumentation.PathOf(module));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "documents are anonymous in every environment");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        root.GetProperty("openapi").GetString().ShouldStartWith("3.1");
        root.GetProperty("info").GetProperty("title").GetString().ShouldBe($"SmartSpare {char.ToUpperInvariant(module[0])}{module[1..]} API");
        if (root.TryGetProperty("servers", out var servers))
        {
            servers.GetArrayLength().ShouldBe(0, "no server addresses, internal or other (ADR 0019 §3)");
        }
        foreach (var path in root.GetProperty("paths").EnumerateObject().Select(p => p.Name))
        {
            path.ShouldStartWith($"/api/v1/{module}/", Case.Sensitive, "no /internal, operational or other module's path (ADR 0019 §3)");
        }
    }

    [Theory]
    [InlineData("api/v1/inventory/parts", "inventory", true)]
    [InlineData("/api/v1/inventory/parts/{id}", "inventory", true)]
    [InlineData("api/inventory/ping", "inventory", false)]
    [InlineData("internal/v1/inventory/stock", "inventory", false)]
    [InlineData("api/v1/fleet/machines", "inventory", false)]
    [InlineData("health/ready", "inventory", false)]
    public void Only_the_modules_versioned_business_paths_are_documented(string path, string module, bool documented) =>
        ApiDocumentation.IsDocumented(path, module).ShouldBe(documented);

    [Fact]
    public void Every_versioned_business_endpoint_requires_authorization()
    {
        var anonymous = _host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText);

        anonymous.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_api_reference_is_public_and_runs_only_its_nonce_script()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync(ApiReference.Path + "/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var scriptSource = policy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        var nonce = NonceSource().Match(scriptSource).Groups["nonce"].Value;
        nonce.Length.ShouldBeGreaterThanOrEqualTo(22, "at least 128 bits");
        scriptSource.ShouldBe($"script-src 'self' 'nonce-{nonce}'");

        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain($"nonce=\"{nonce}\"");
        html.ShouldNotContain("fonts.scalar.com");
        html.ShouldNotContain("cdn.jsdelivr.net");

        using var again = await browser.GetAsync(ApiReference.Path + "/");
        again.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain(nonce, Case.Sensitive, "a fresh nonce per response");
    }

    [Fact]
    public async Task Other_pages_keep_the_stage_3_policy_without_a_nonce()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync("/account/login");

        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self' 'wasm-unsafe-eval'");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain("nonce-");
    }

    [GeneratedRegex("'nonce-(?<nonce>[A-Za-z0-9+/=]+)'")]
    private static partial Regex NonceSource();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
