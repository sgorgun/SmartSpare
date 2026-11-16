using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartSpare.Host.IntegrationTests;

// In-memory host, no external dependencies: deliberately not tagged Category=Integration,
// so it runs in CI. Tests that need Docker (Testcontainers) carry that tag.
public sealed class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string> Modules => ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Ping_ReturnsOk_ForEveryModule(string module)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/api/{module}/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PingBody>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Module.ShouldBe(module);
        body.Status.ShouldBe("ok");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnOk(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetails()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/unknown/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    private sealed record PingBody(string Module, string Status);
}
