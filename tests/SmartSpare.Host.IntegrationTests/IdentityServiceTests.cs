using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The stage 3 sign-in matrix with the single host's BFF calling a real Identity service host over HTTP
/// (<see cref="HttpCredentialVerifier"/>, STAGE_4_PLAN 4d acceptance): the gateway's verifier behaves exactly like the
/// in-process one, including uniform failures, lockout, rehash and session revocation.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class SignInThroughIdentityServiceTests(IdentityDatabaseFixture database) : SignInMatrix(database)
{
    private IdentityServiceFactory? _identity;

    protected override string Suffix => ".svc";

    protected override HostFactory CreateHost()
    {
        var host = new HostFactory
        {
            ConnectionString = Database.ApiConnectionString,
            ExtraServices = services => services.AddScoped<ICredentialVerifier>(_ => _identity!.Verifier()),
        };

        // One clock for both: lockout and session checks run in the Identity service.
        _identity ??= new IdentityServiceFactory(Database.ApiConnectionString, host.Time);
        return host;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_identity is not null)
        {
            await _identity.DisposeAsync();
        }
    }
}

/// <summary>The Identity service host itself: its internal endpoints, their policy, and the 503 of the BFF.</summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class IdentityServiceTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private IdentityServiceFactory _identity = null!;

    public ValueTask InitializeAsync()
    {
        _identity = new IdentityServiceFactory(database.ApiConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _identity.DisposeAsync();

    [Fact]
    public async Task Internal_checks_need_the_gateway_itself()
    {
        var body = new CredentialCheckRequest("nobody", "wrong", null);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AppClaimTypes.Subject, Guid.NewGuid().ToString()), new Claim(AppClaimTypes.Name, "u"), new Claim(AppClaimTypes.Role, AppRoles.Administrator)]));

        using var anonymous = _identity.Client(null);
        using var asUser = _identity.Client(IdentityServiceFactory.GatewayIssuer.CreateToken(user, "sid"));
        using var asOtherService = _identity.Client(IdentityServiceFactory.GatewayIssuer.CreateServiceToken("inventory"));
        using var asGateway = _identity.Client(IdentityServiceFactory.GatewayIssuer.CreateServiceToken("gateway"));

        (await anonymous.PostAsJsonAsync(IdentityInternalApi.CredentialChecks, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await asUser.PostAsJsonAsync(IdentityInternalApi.CredentialChecks, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await asOtherService.PostAsJsonAsync(IdentityInternalApi.CredentialChecks, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await asGateway.PostAsJsonAsync(IdentityInternalApi.CredentialChecks, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_service_accepts_no_cookie_and_needs_no_antiforgery_token()
    {
        // JwtBearer is its only scheme: a browser's cookie authenticates nothing here.
        using var client = _identity.Client(null);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-smartspare=anything");

        using var response = await client.GetAsync(new Uri("/api/identity/ping", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unreachable_identity_service_is_a_503_never_a_wrong_password()
    {
        using var unreachable = new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://identity.invalid") };
        await using var host = new HostFactory
        {
            ConnectionString = database.ApiConnectionString,
            ExtraServices = services => services.AddScoped<ICredentialVerifier>(_ => new HttpCredentialVerifier(unreachable)),
        };
        using var browser = host.NewBrowser();

        using var response = await browser.LoginAsync("anyone", "anything");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var problem = await Problem.ReadAsync(response);
        problem.Code.ShouldBe(ProblemCodes.ServiceUnavailable);
        problem.TraceId.ShouldNotBeNullOrEmpty();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (test).");
    }
}
