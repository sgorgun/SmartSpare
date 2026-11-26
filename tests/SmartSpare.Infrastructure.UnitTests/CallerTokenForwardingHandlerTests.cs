using System.Net;
using Microsoft.AspNetCore.Http;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>Contracts queries between services carry the caller's internal token (ARCHITECTURE_DECISIONS section 4).</summary>
public sealed class CallerTokenForwardingHandlerTests
{
    [Fact]
    public async Task The_bearer_token_of_the_current_request_is_forwarded()
    {
        var seen = await SendAsync("Bearer abc.def.ghi");

        seen.ShouldBe("Bearer abc.def.ghi");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    public async Task Nothing_but_a_bearer_token_is_forwarded(string? authorization) =>
        (await SendAsync(authorization)).ShouldBeNull();

    [Fact]
    public async Task Without_a_request_nothing_is_forwarded() =>
        (await SendAsync(authorization: null, withRequest: false)).ShouldBeNull();

    private static async Task<string?> SendAsync(string? authorization, bool withRequest = true)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        var recorder = new Recorder();
        var handler = new CallerTokenForwardingHandler(new HttpContextAccessor { HttpContext = withRequest ? context : null }) { InnerHandler = recorder };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(new Uri("http://inventory/internal/v1/inventory/warehouses"), TestContext.Current.CancellationToken);
        return recorder.Authorization;
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
