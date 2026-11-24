using System.Diagnostics;
using System.Net;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// #60: every ProblemDetails carries the W3C trace id of its request, whatever wrote it: business errors, validation,
/// 401/403, antiforgery, 429 (in <see cref="RateLimitTests"/>), sign-in failures (in <see cref="BffTests"/>) and
/// unhandled exceptions. Also the antiforgery rule on <c>/api</c>: cookie requests need the token, bearer requests don't.
/// </summary>
public sealed class ErrorResponseTests(SecurityTestHost host) : IClassFixture<SecurityTestHost>
{
    private static readonly TestUser Someone = TestUser.Create("someone.errors", [AppRoles.BrandManager]);

    public static TheoryData<string, int, string?> ErrorEndpoints => new()
    {
        { "/api/test/business-error", 422, "SS201" },
        { "/api/test/validation-error", 400, "Validation" },
        { "/api/test/boom", 500, null },
    };

    [Theory]
    [MemberData(nameof(ErrorEndpoints))]
    public async Task Problem_details_carry_the_w3c_trace_id_of_the_request(string path, int status, string? code)
    {
        using var browser = host.NewBrowser();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        browser.Client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var response = await browser.GetAsync(path, bearer: host.Token(Someone));

        ((int)response.StatusCode).ShouldBe(status);
        var problem = await Problem.ReadAsync(response);
        problem.Code.ShouldBe(code);
        problem.TraceId.ShouldBe(traceId, "the trace id is the W3C trace id propagated with traceparent");
    }

    [Fact]
    public async Task An_unsafe_api_request_with_the_cookie_needs_the_antiforgery_token()
    {
        host.Verifier.Add(Someone);
        using var browser = host.NewBrowser();
        await browser.LoginOrFailAsync(Someone);

        using var withoutToken = await browser.PostAsync("/api/test/echo");
        withoutToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Problem.ReadAsync(withoutToken);
        problem.Code.ShouldBe(ProblemCodes.AntiforgeryFailed);
        problem.TraceId.ShouldNotBeNullOrEmpty();

        using var withToken = await browser.PostAsync("/api/test/echo", token: await browser.AntiforgeryTokenAsync());
        withToken.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_request_with_an_internal_token_needs_no_antiforgery_token()
    {
        using var browser = host.NewBrowser();

        using var response = await browser.PostAsync("/api/test/echo", bearer: host.Token(Someone));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_bearer_token_does_not_skip_antiforgery_on_anonymous_endpoints()
    {
        // The filter skips only requests authenticated by an internal token, not requests that merely send one.
        using var browser = host.NewBrowser();

        using var response = await browser.PostAsync("/bff/login", new { UserName = "x", Password = "y" }, bearer: "not-a-token");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(response)).Code.ShouldBe(ProblemCodes.AntiforgeryFailed);
    }
}
