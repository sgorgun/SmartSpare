using System.Net;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The <c>/bff</c> endpoints and the cookie in the real host (STAGE_3_PLAN 9.2), against a fake
/// <c>ICredentialVerifier</c> and no database: this also proves the BFF needs nothing but the Identity contract, which is
/// how the gateway uses it (ADR 0016). Real credential checks are in <see cref="SignInTests"/>.
/// </summary>
public sealed class BffTests(FakeIdentityHostFactory factory) : IClassFixture<FakeIdentityHostFactory>
{
    [Fact]
    public async Task Sign_in_issues_the_session_cookie_with_the_required_flags()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.flags", [AppRoles.BrandManager], [1, 5]));
        using var browser = factory.NewBrowser();

        using var response = await browser.LoginAsync(user.UserName, user.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookie = SessionCookie(response);
        cookie.ShouldStartWith($"{BffDefaults.CookieName}=");
        var attributes = Attributes(cookie);
        attributes.ShouldContain("httponly");
        attributes.ShouldContain("secure");
        attributes.ShouldContain("samesite=strict");
        attributes.ShouldContain("path=/");
        attributes.ShouldNotContain(a => a.StartsWith("domain", StringComparison.Ordinal));
        attributes.ShouldNotContain(a => a.StartsWith("expires", StringComparison.Ordinal), "without RememberMe the cookie lives for the browser session");
    }

    [Fact]
    public async Task Remember_me_makes_the_cookie_persistent_for_at_most_eight_hours()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.remember", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();

        using var response = await browser.LoginAsync(user.UserName, user.Password, rememberMe: true);

        var expires = Attributes(SessionCookie(response)).Single(a => a.StartsWith("expires=", StringComparison.Ordinal));
        var expiresAt = DateTimeOffset.Parse(expires["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        (expiresAt - factory.Time.GetUtcNow()).ShouldBeLessThanOrEqualTo(BffDefaults.SessionLifetime + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Me_returns_the_claims_of_the_session_and_anonymous_without_one()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.me", [AppRoles.BrandManager, AppRoles.Sponsor], [5, 1], warehouse: 3));
        using var browser = factory.NewBrowser();

        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse();

        await browser.LoginOrFailAsync(user);
        var me = await browser.MeAsync();

        me.IsAuthenticated.ShouldBeTrue();
        me.UserId.ShouldBe(user.Id);
        me.UserName.ShouldBe(user.UserName);
        me.Roles.ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor]);
        me.BrandIds.ShouldBe([(short)1, (short)5]);
        me.HomeWarehouseId.ShouldBe((short)3);
    }

    [Fact]
    public async Task Every_sign_in_failure_gets_the_same_response()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.uniform", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();

        var failures = new List<Problem>();
        foreach (var (name, password) in new[] { (user.UserName, "wrong password"), ("no.such.user", user.Password), (string.Empty, string.Empty) })
        {
            using var response = await browser.LoginAsync(name, password);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await Problem.ReadAsync(response);
            problem.TraceId.ShouldNotBeNullOrEmpty();
            failures.Add(problem.WithoutTraceId());
        }

        failures.Distinct().ShouldHaveSingleItem().Code.ShouldBe(ProblemCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Sign_in_without_an_antiforgery_token_is_rejected()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.csrf", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();

        using var response = await browser.PostAsync("/bff/login", new { user.UserName, user.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Problem.ReadAsync(response);
        problem.Code.ShouldBe(ProblemCodes.AntiforgeryFailed);
        problem.TraceId.ShouldNotBeNullOrEmpty();
        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_issued_before_sign_in_is_rejected_after_it()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.rebind", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();
        var anonymousToken = await browser.AntiforgeryTokenAsync();
        using (var login = await browser.LoginAsync(user.UserName, user.Password, token: anonymousToken))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var stale = await browser.PostAsync("/bff/logout", token: anonymousToken);
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem.ReadAsync(stale)).Code.ShouldBe(ProblemCodes.AntiforgeryFailed);

        using var fresh = await browser.PostAsync("/bff/logout", token: await browser.AntiforgeryTokenAsync());
        fresh.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_clears_the_cookie_and_ends_the_session()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.logout", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();
        await browser.LoginOrFailAsync(user);

        using var response = await browser.PostAsync("/bff/logout", token: await browser.AntiforgeryTokenAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cleared = SessionCookie(response);
        Attributes(cleared).ShouldContain(a => a.StartsWith("expires=thu, 01 jan 1970", StringComparison.Ordinal));
        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse();
        using var api = await browser.GetAsync("/api/identity/ping");
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_needs_a_session()
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.PostAsync("/bff/logout", token: await browser.AntiforgeryTokenAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Problem.ReadAsync(response)).Code.ShouldBe(ProblemCodes.Unauthenticated);
    }

    [Fact]
    public async Task The_session_is_checked_every_five_minutes_and_a_revoked_one_ends()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.revoked", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();
        await browser.LoginOrFailAsync(user);
        var checks = factory.Users.SessionChecks;

        user.Stamp = Guid.NewGuid().ToString("N");     // e.g. password changed or user deactivated
        factory.Time.Advance(TimeSpan.FromMinutes(4));
        (await browser.MeAsync()).IsAuthenticated.ShouldBeTrue("the stamp is not checked before the interval");
        factory.Users.SessionChecks.ShouldBe(checks);

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        using var response = await browser.GetAsync("/api/identity/ping");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.Users.SessionChecks.ShouldBeGreaterThan(checks);
        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task Changed_roles_renew_the_cookie_at_the_next_session_check()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.promoted", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();
        await browser.LoginOrFailAsync(user);

        user.CurrentRoles = [AppRoles.BrandManager, AppRoles.Sponsor];
        factory.Time.Advance(BffDefaults.SessionCheckInterval);
        using var response = await browser.GetAsync("/bff/me");

        SessionCookie(response).ShouldStartWith($"{BffDefaults.CookieName}=", customMessage: "the cookie is re-issued with the new claims");
        (await browser.MeAsync()).Roles.ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor]);
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(BffDefaults.CookieName + "=", StringComparison.Ordinal));

    private static List<string> Attributes(string setCookie) =>
        [.. setCookie.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant())];
}
