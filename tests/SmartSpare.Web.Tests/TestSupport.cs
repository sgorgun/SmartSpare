using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>A bUnit context with the client's real services: MudBlazor, localization (en-US resx), messages.</summary>
public abstract class ClientTestContext : BunitContext
{
    protected ClientTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;          // MudBlazor calls JS interop (STAGE_3_PLAN 9.3)
        Services.AddMudServices();
        Services.AddLocalization();
        Services.AddScoped<ProblemMessages>();
    }

    /// <summary>
    /// Renders as the interactive WebAssembly renderer the client runs in. Called after the test class registered its
    /// services: touching the renderer freezes the service collection.
    /// </summary>
    protected void UseWebAssemblyRenderer() =>
        Renderer.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("WebAssembly", isInteractive: true));

    protected static ClaimsPrincipal User(string name, params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(AppClaimTypes.Role, r)).Append(new Claim(AppClaimTypes.Name, name)),
            "test",
            AppClaimTypes.Name,
            AppClaimTypes.Role));

    protected static ClaimsPrincipal Anonymous { get; } = new(new ClaimsIdentity());

    protected static Task<AuthenticationState> State(ClaimsPrincipal user) => Task.FromResult(new AuthenticationState(user));
}

/// <summary>An <see cref="IdentityClient"/> whose answers the test decides.</summary>
public sealed class FakeIdentityClient() : IdentityClient(new HttpClient())
{
    public List<LoginRequest> Logins { get; } = [];

    public Func<LoginRequest, Task<ApiResult<NoContent>>> OnLogin { get; set; } = _ => Task.FromResult(ApiResult.Success(new NoContent()));

    public CurrentUserResponse Me { get; set; } = CurrentUserResponse.Anonymous;

    public override Task<ApiResult<NoContent>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        Logins.Add(request);
        return OnLogin(request);
    }

    public override Task<ApiResult<CurrentUserResponse>> MeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(Me));

    public override Task<ApiResult<NoContent>> LogoutAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new NoContent()));
}

/// <summary>An antiforgery state with a fixed request token.</summary>
public sealed class FixedAntiforgery(string? token) : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken? GetAntiforgeryToken() => token is null ? null : new AntiforgeryRequestToken(token, "__RequestVerificationToken");
}

/// <summary>The end of a handler chain: records the request and answers with a fixed response.</summary>
public sealed class StubHandler(HttpStatusCode status, object? problem = null) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        var response = new HttpResponseMessage(status);
        if (problem is not null)
        {
            response.Content = JsonContent.Create(problem, mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue(ProblemInfo.MediaType));
        }

        return Task.FromResult(response);
    }
}
