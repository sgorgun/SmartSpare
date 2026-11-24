using System.Net;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

// In-memory host, no external dependencies: deliberately not tagged Category=Integration,
// so it runs in CI. Tests that need Docker (Testcontainers) carry that tag.
public sealed class SmokeTests(HostFactory factory) : IClassFixture<HostFactory>
{
    public static TheoryData<string> Modules => ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    private static readonly TestUser Someone = TestUser.Create("someone", []);

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Ping_ReturnsOk_ForEveryModule_ToAnAuthenticatedUser(string module)
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync($"/api/{module}/ping", bearer: factory.Token(Someone.Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PingBody>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Module.ShouldBe(module);
        body.Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Module_endpoints_require_an_authenticated_user_and_answer_401_without_a_redirect()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/api/identity/ping");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        var problem = await Problem.ReadAsync(response);
        problem.Code.ShouldBe(ProblemCodes.Unauthenticated);
        problem.TraceId.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnOk_WithoutSignIn(string path)
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/api/unknown/ping", bearer: factory.Token(Someone.Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem.ReadAsync(response)).TraceId.ShouldNotBeNullOrEmpty();
    }

    private sealed record PingBody(string Module, string Status);
}
