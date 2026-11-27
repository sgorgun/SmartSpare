using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Bff;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// A browser against a test server: keeps cookies (HTTPS, so the <c>__Host-</c> cookies are sent), sends the
/// antiforgery header like the Blazor client, and comes from its own client IP (own rate-limit partition).
/// </summary>
internal sealed class Browser(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    public static Browser Over(HttpMessageHandler serverHandler, string clientIp)
    {
        var client = new HttpClient(new CookieContainerHandler(serverHandler)) { BaseAddress = new Uri("https://localhost") };
        client.DefaultRequestHeaders.Add(TestClientIp.Header, clientIp);
        return new Browser(client);
    }

    public async Task<string> AntiforgeryTokenAsync()
    {
        var response = await Client.GetFromJsonAsync<AntiforgeryTokenResponse>(new Uri("/bff/antiforgery", UriKind.Relative), Ct);
        response.ShouldNotBeNull();
        response.HeaderName.ShouldBe(BffDefaults.AntiforgeryHeaderName);
        return response.RequestToken;
    }

    public async Task<HttpResponseMessage> LoginAsync(string userName, string password, bool rememberMe = false, string? token = null)
    {
        token ??= await AntiforgeryTokenAsync();
        return await PostAsync("/bff/login", new LoginRequest(userName, password, rememberMe), token);
    }

    public async Task LoginOrFailAsync(TestUser user)
    {
        using var response = await LoginAsync(user.UserName, user.Password);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>An unsafe request; with <paramref name="token"/> = <see langword="null"/> it carries no antiforgery header.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? token = null, string? bearer = null) =>
        SendAsync(HttpMethod.Post, path, body, token, bearer);

    /// <summary>An unsafe PUT, like <see cref="PostAsync"/>.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, object? body = null, string? token = null, string? bearer = null) =>
        SendAsync(HttpMethod.Put, path, body, token, bearer);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? token, string? bearer)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType()),
        };
        if (token is not null)
        {
            request.Headers.Add(BffDefaults.AntiforgeryHeaderName, token);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await Client.SendAsync(request, Ct);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await Client.SendAsync(request, Ct);
    }

    public async Task<CurrentUserResponse> MeAsync(string? bearer = null)
    {
        using var response = await GetAsync("/bff/me", bearer);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CurrentUserResponse>(Ct))!;
    }

    public void Dispose() => Client.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Cookie handling for a raw test-server handler (WebApplicationFactory clients have their own).</summary>
    private sealed class CookieContainerHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    _cookies.SetCookies(request.RequestUri!, setCookie);
                }
            }

            return response;
        }
    }
}

/// <summary>A parsed ProblemDetails response.</summary>
internal sealed record Problem(int Status, string? Title, string? Detail, string? Code, string? TraceId)
{
    public static async Task<Problem> ReadAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        return new Problem(
            root.GetProperty("status").GetInt32(),
            Text(root, "title"),
            Text(root, "detail"),
            Text(root, "code"),
            Text(root, "traceId"));
    }

    /// <summary>The body without the per-request trace id, for comparing responses.</summary>
    public Problem WithoutTraceId() => this with { TraceId = null };

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Sets the client IP of a test request from a header, so each test has its own rate-limit partition.</summary>
internal sealed class TestClientIp : IStartupFilter
{
    public const string Header = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            return nextMiddleware(context);
        });
        next(app);
    };

    private static int s_next;

    /// <summary>A client IP no other test uses.</summary>
    public static string Unique() => $"10.{(Interlocked.Increment(ref s_next) >> 16) & 255}.{(s_next >> 8) & 255}.{s_next & 255}";
}
