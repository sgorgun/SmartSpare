extern alias gateway;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Bff;
using gateway::SmartSpare.Gateway;
using SmartSpare.Host.IntegrationTests.Support;
using Yarp.ReverseProxy;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The gateway against a real Identity service host on PostgreSQL (STAGE_4_PLAN 4e acceptance): sign-in, a module
/// endpoint reached with a minted token, browser headers removed, antiforgery in front of the proxy, and
/// <c>/internal</c> and service health never forwarded.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class GatewayTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private IdentityServiceFactory _identity = null!;
    private GatewayFactory _gateway = null!;

    public ValueTask InitializeAsync()
    {
        _identity = new IdentityServiceFactory(database.ApiConnectionString);
        _gateway = new GatewayFactory(_identity);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _identity.DisposeAsync();
    }

    [Fact]
    public async Task A_user_signs_in_through_the_gateway_and_reaches_the_identity_service()
    {
        await database.CreateUserAsync("gw.signin", [AppRoles.BrandManager], ["CLAAS"]);
        using var browser = await SignInAsync("gw.signin");

        var me = await browser.MeAsync();
        using var ping = await browser.GetAsync("/api/identity/ping");

        me.IsAuthenticated.ShouldBeTrue();
        me.UserName.ShouldBe("gw.signin");
        ping.StatusCode.ShouldBe(HttpStatusCode.OK, "the Identity service accepted the gateway's token for the user");
    }

    [Fact]
    public async Task A_proxied_request_carries_the_users_internal_token_and_no_browser_headers()
    {
        var id = await database.CreateUserAsync("gw.token", [AppRoles.InventoryPlanner], []);
        using var browser = await SignInAsync("gw.token");

        using var first = await browser.GetAsync("/api/v1/inventory/parts?page=2");
        using var second = await browser.GetAsync("/api/inventory/ping");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = await first.Content.ReadFromJsonAsync<ReceivedRequest>(Ct);
        received!.Service.ShouldBe("inventory");
        received.Path.ShouldBe("/api/v1/inventory/parts?page=2");
        received.Headers.ShouldNotContainKey("Cookie");
        received.Headers.ShouldNotContainKey(HttpHeaderNames.Antiforgery);

        var authorization = received.Headers["Authorization"];
        authorization.ShouldStartWith("Bearer ");
        var token = authorization["Bearer ".Length..];
        var jwt = new JsonWebToken(token);
        jwt.Subject.ShouldBe(id.ToString());
        jwt.GetClaim(AppClaimTypes.Role).Value.ShouldBe(AppRoles.InventoryPlanner);
        jwt.Audiences.ShouldBe(["smartspare-internal"]);

        var again = await second.Content.ReadFromJsonAsync<ReceivedRequest>(Ct);
        again!.Headers["Authorization"].ShouldBe(received.Headers["Authorization"], "one token per session until shortly before it expires");
    }

    [Fact]
    public async Task Anonymous_api_requests_get_401_and_nothing_is_forwarded()
    {
        using var browser = _gateway.NewBrowser();

        using var response = await browser.GetAsync("/api/v1/inventory/parts");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _gateway.Recorder.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unsafe_request_without_an_antiforgery_token_is_refused_before_the_proxy()
    {
        await database.CreateUserAsync("gw.csrf", [AppRoles.InventoryPlanner], []);
        using var browser = await SignInAsync("gw.csrf");

        using var refused = await browser.PostAsync("/api/v1/inventory/transfers", new { });
        using var accepted = await browser.PostAsync("/api/v1/inventory/transfers", new { }, token: await browser.AntiforgeryTokenAsync());

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(refused)).Code.ShouldBe(ProblemCodes.AntiforgeryFailed);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Recorder.Received.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Internal_endpoints_and_service_health_are_never_forwarded()
    {
        await database.CreateUserAsync("gw.internal", [AppRoles.Administrator], []);
        using var browser = await SignInAsync("gw.internal");
        var token = await browser.AntiforgeryTokenAsync();

        foreach (var module in GatewayRoutes.Modules)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                foreach (var path in new[] { $"/internal/v1/{module}/x", $"/{module}/health/ready", $"/api/v1/{module}/../../internal/v1/{module}/x" })
                {
                    using var request = new HttpRequestMessage(method, path);
                    request.Headers.Add(HttpHeaderNames.Antiforgery, token);
                    using var response = await browser.Client.SendAsync(request, Ct);
                    ((int)response.StatusCode).ShouldNotBe(200, $"{method} {path}");
                }
            }
        }

        _gateway.Recorder.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Warmup_wakes_every_service_anonymously_and_is_rate_limited()
    {
        using var browser = _gateway.NewBrowser();

        using var first = await browser.GetAsync("/warmup");
        using var second = await browser.GetAsync("/warmup");
        using var third = await browser.GetAsync("/warmup");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = (await first.Content.ReadFromJsonAsync<WarmupEndpoint.WarmupResult[]>(Ct))!;
        results.Select(r => r.Service).ShouldBe(GatewayRoutes.Modules);
        results.ShouldAllBe(r => r.Status == 200);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void The_route_table_has_two_literal_routes_per_module_and_none_can_match_internal()
    {
        var routes = _gateway.Services.GetRequiredService<IProxyStateLookup>().GetRoutes().Select(r => r.Config).ToList();

        routes.Select(r => r.RouteId).ShouldBe(
            GatewayRoutes.Modules.SelectMany(m => new[] { $"api-{m}", $"ops-{m}", $"openapi-{m}" }), ignoreOrder: true);

        var probes = GatewayRoutes.Modules
            .SelectMany(m => new[] { $"/internal/v1/{m}/x", $"/internal/{m}", $"/internal/v1/{m}/credential-checks", $"/health/ready", $"/{m}/health/live" })
            .Append("/internal/v1/identity/credential-checks")
            .ToList();
        foreach (var route in routes)
        {
            var pattern = RoutePatternFactory.Parse(route.Match.Path!);
            pattern.Parameters.Select(p => p.Name).ShouldBeSubsetOf(["rest"], $"{route.RouteId} names its module literally");
            var matcher = new TemplateMatcher(TemplateParser.Parse(route.Match.Path!.TrimStart('/')), new());
            probes.ShouldAllBe(p => !matcher.TryMatch(p, new()), route.RouteId);
            if (route.RouteId.StartsWith("ops-", StringComparison.Ordinal))
            {
                GatewayRoutes.Modules.ShouldAllBe(m => !matcher.TryMatch($"/api/v1/{m}/x", new()), $"{route.RouteId} never takes a versioned path");
            }
        }
    }

    [Fact]
    public async Task Module_documents_are_proxied_anonymously_without_a_token()
    {
        using var browser = _gateway.NewBrowser();

        using var identity = await browser.GetAsync("/openapi/identity.json");
        using var inventory = await browser.GetAsync("/openapi/inventory.json");
        using var post = await browser.PostAsync("/openapi/inventory.json", null, token: await browser.AntiforgeryTokenAsync());

        identity.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await identity.Content.ReadAsStringAsync(Ct)).ShouldContain("SmartSpare Identity API", Case.Sensitive, "the Identity service's own document");
        var received = await inventory.Content.ReadFromJsonAsync<ReceivedRequest>(Ct);
        received!.Path.ShouldBe("/openapi/inventory.json");
        received.Headers.ShouldNotContainKey("Authorization");
        ((int)post.StatusCode).ShouldNotBe(200, "documents are GET only");
    }

    [Fact]
    public async Task The_gateway_generates_the_session_document_itself_and_still_proxies_module_documents()
    {
        using var browser = _gateway.NewBrowser();

        using var bff = await browser.GetAsync("/openapi/bff.json");

        bff.StatusCode.ShouldBe(HttpStatusCode.OK, "anonymous, like the module documents");
        var document = await bff.Content.ReadAsStringAsync(Ct);
        document.ShouldContain("SmartSpare Session API (BFF)");
        document.ShouldContain("\"/bff/login\"");
        _gateway.Recorder.Received.ShouldBeEmpty("the /bff endpoints are the gateway's own, so no service is asked");

        // The literal openapi-{module} routes still win over the gateway's own /openapi/{documentName}.json endpoint.
        using var inventory = await browser.GetAsync("/openapi/inventory.json");
        (await inventory.Content.ReadFromJsonAsync<ReceivedRequest>(Ct))!.Path.ShouldBe("/openapi/inventory.json");
    }

    [Fact]
    public async Task The_gateway_serves_the_api_reference_with_a_nonce()
    {
        using var browser = _gateway.NewBrowser();

        using var response = await browser.GetAsync("/scalar/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("'nonce-");
    }

    private async Task<Browser> SignInAsync(string userName)
    {
        var browser = _gateway.NewBrowser();
        using var login = await browser.LoginAsync(userName, IdentityDatabaseFixture.Password);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return browser;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
