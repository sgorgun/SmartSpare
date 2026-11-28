using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// One principal in both topologies (ADR 0016 section 4): the single host accepts the cookie and the internal token
/// through the policy scheme, and module endpoints behave the same with either. Invalid tokens are rejected.
/// </summary>
public sealed class InternalTokenTests(FakeIdentityHostFactory factory) : IClassFixture<FakeIdentityHostFactory>
{
    /// <summary>The size budget of an internal token (ADR 0028): it carries the permissions, the cookie does not.</summary>
    private const int TokenBudgetBytes = 8 * 1024;

    [Fact]
    public void A_token_with_every_permission_stays_within_the_8_KB_budget()
    {
        // The worst case: every permission of the catalogue (a superadministrator), every role, brands and hubs.
        var claims = new List<Claim> { new(AppClaimTypes.Subject, Guid.NewGuid().ToString()), new(AppClaimTypes.Name, "superadmin.smartspare") };
        claims.AddRange(AppRoles.All.Append(AppRoles.Superadministrator).Select(r => new Claim(AppClaimTypes.Role, r)));
        claims.AddRange(Permissions.All.Select(p => new Claim(AppClaimTypes.Permission, p.Name)));
        claims.AddRange(Enumerable.Range(1, 12).Select(b => new Claim(AppClaimTypes.Brand, b.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        claims.AddRange(Enumerable.Range(1, 6).Select(h => new Claim(AppClaimTypes.Warehouse, h.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var token = factory.Token(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        TestContext.Current.SendDiagnosticMessage($"Internal token with all {Permissions.All.Count} permissions: {Encoding.ASCII.GetByteCount(token)} bytes");
        Encoding.ASCII.GetByteCount(token).ShouldBeLessThan(TokenBudgetBytes);
        var jwt = new JsonWebToken(token);
        jwt.Claims.ShouldNotContain(c => c.Type == AppClaimTypes.Permission, "names never travel; the compact bitset does");
        PermissionBitset.Decode(jwt.GetClaim(AppClaimTypes.PermissionSet).Value).Count.ShouldBe(Permissions.All.Count);
        jwt.GetClaim(AppClaimTypes.PermissionVersion).Value.ShouldBe(Permissions.CatalogueVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The superadministrator's token at the catalogue sizes expected later (ADR 0028): only the bitset grows with the
    /// catalogue, by one bit per code, so the projection adds the longer bitset, base64url-encoded again inside the JWT
    /// payload, to today's measured worst case. Names would cost about 40 bytes per permission instead.
    /// </summary>
    [Theory]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(300)]
    public void The_superadministrator_token_stays_small_at_the_projected_catalogue_size(int permissions)
    {
        var claims = new List<Claim> { new(AppClaimTypes.Subject, Guid.NewGuid().ToString()), new(AppClaimTypes.Name, "superadmin.smartspare") };
        claims.AddRange(AppRoles.All.Append(AppRoles.Superadministrator).Select(r => new Claim(AppClaimTypes.Role, r)));
        claims.AddRange(Permissions.All.Select(p => new Claim(AppClaimTypes.Permission, p.Name)));
        claims.AddRange(Enumerable.Range(1, 12).Select(b => new Claim(AppClaimTypes.Brand, b.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        claims.AddRange(Enumerable.Range(1, 6).Select(h => new Claim(AppClaimTypes.Warehouse, h.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var token = factory.Token(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        var measured = Encoding.ASCII.GetByteCount(token);
        var bitsetNow = new JsonWebToken(token).GetClaim(AppClaimTypes.PermissionSet).Value.Length;

        var bitsetThen = System.Buffers.Text.Base64Url.GetEncodedLength((permissions / 8) + 1);
        var projected = measured + (int)Math.Ceiling((bitsetThen - bitsetNow) * 4 / 3.0);

        TestContext.Current.SendDiagnosticMessage(
            $"Superadministrator token: {measured} bytes with {Permissions.All.Count} permissions; projected {projected} bytes with {permissions}");
        projected.ShouldBeLessThan(1500, "the bitset keeps the worst case far below the ~6 KB line at which a compact encoding was required");
        projected.ShouldBeLessThan(TokenBudgetBytes);
    }

    [Fact]
    public async Task Module_endpoints_give_the_same_results_with_the_cookie_and_with_an_internal_token()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.both", [AppRoles.BrandManager, AppRoles.Sponsor], [1, 5], warehouse: 2));
        using var withCookie = factory.NewBrowser();
        await withCookie.LoginOrFailAsync(user);
        using var withToken = factory.NewBrowser();
        var token = factory.Token(user.Principal());

        foreach (var path in new[] { "/api/identity/ping", "/api/inventory/ping" })
        {
            using var cookieResponse = await withCookie.GetAsync(path);
            using var tokenResponse = await withToken.GetAsync(path, bearer: token);

            tokenResponse.StatusCode.ShouldBe(cookieResponse.StatusCode);
            (await tokenResponse.Content.ReadAsStringAsync(Ct)).ShouldBe(await cookieResponse.Content.ReadAsStringAsync(Ct));
        }

        (await withToken.MeAsync(bearer: token)).ShouldBeEquivalentTo(await withCookie.MeAsync());
    }

    [Fact]
    public async Task The_token_carries_the_claims_of_the_session_but_not_the_security_stamp()
    {
        var user = factory.Users.Add(TestUser.Create("tomas.claims", [AppRoles.InventoryPlanner], [2], warehouse: 4));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(user.Claims()));

        var token = new JsonWebToken(factory.Token(principal));

        token.Issuer.ShouldBe(InternalTokenDefaults.Issuer);
        token.Audiences.ShouldBe([InternalTokenDefaults.Audience]);
        token.Alg.ShouldBe(SecurityAlgorithms.EcdsaSha256);
        token.Kid.ShouldNotBeNullOrEmpty();
        (token.ValidTo - token.IssuedAt).ShouldBe(InternalTokenDefaults.Lifetime);
        token.Subject.ShouldBe(user.Id.ToString());
        token.GetClaim(AppClaimTypes.Role).Value.ShouldBe(AppRoles.InventoryPlanner);
        token.GetClaim(AppClaimTypes.Brand).Value.ShouldBe("2");
        token.GetClaim(AppClaimTypes.Warehouse).Value.ShouldBe("4");
        token.TryGetClaim(AppClaimTypes.SessionId, out _).ShouldBeTrue();
        token.TryGetClaim(JwtRegisteredClaimNames.Jti, out _).ShouldBeTrue();
        token.Claims.ShouldNotContain(c => c.Type == Modules.Identity.Contracts.IdentityClaimTypes.SecurityStamp);
    }

    public static TheoryData<string> InvalidTokens => new()
    {
        "expired",
        "wrong audience",
        "wrong issuer",
        "signed with another key",
        "HS256",
        "alg none",
        "not a token",
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_internal_tokens_are_rejected(string kind)
    {
        using var browser = factory.NewBrowser();

        using var response = await browser.GetAsync("/api/identity/ping", bearer: Invalid(kind));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Problem.ReadAsync(response)).Code.ShouldBe(ProblemCodes.Unauthenticated);
    }

    [Fact]
    public async Task A_bearer_header_is_never_combined_with_the_cookie()
    {
        // The policy scheme sends a bearer request to the internal-token scheme only: a valid cookie can't rescue it.
        var user = factory.Users.Add(TestUser.Create("tomas.mixed", [AppRoles.BrandManager]));
        using var browser = factory.NewBrowser();
        await browser.LoginOrFailAsync(user);

        using var response = await browser.GetAsync("/api/identity/ping", bearer: Invalid("signed with another key"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_configured_key_every_internal_token_is_rejected()
    {
        await using var withoutKey = new HostFactory { WithSigningKey = false };
        using var browser = withoutKey.NewBrowser();

        using var response = await browser.GetAsync("/api/identity/ping", bearer: factory.Token(TestUser.Create("x", []).Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string Invalid(string kind)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(TestUser.ClaimsFor(Guid.NewGuid(), "attacker", [AppRoles.Administrator], [], null)),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + InternalTokenDefaults.Lifetime,
            SigningCredentials = TestSigning(),
        };
        var handler = new JsonWebTokenHandler();

        switch (kind)
        {
            case "expired":
                descriptor.IssuedAt = descriptor.NotBefore = now.AddMinutes(-10);
                descriptor.Expires = now.AddMinutes(-8);     // beyond the 30 s clock skew
                break;
            case "wrong audience":
                descriptor.Audience = "smartspare-somewhere-else";
                break;
            case "wrong issuer":
                descriptor.Issuer = "someone-else";
                break;
            case "signed with another key":
                descriptor.SigningCredentials = OtherKey();
                break;
            case "HS256":
                descriptor.SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256);
                break;
            case "alg none":
                var header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
                var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(
                    $$"""{"iss":"{{InternalTokenDefaults.Issuer}}","aud":"{{InternalTokenDefaults.Audience}}","sub":"{{Guid.NewGuid()}}","role":"ADMINISTRATOR","exp":{{new DateTimeOffset(now.AddMinutes(2)).ToUnixTimeSeconds()}}}"""));
                return $"{header}.{payload}.";
            case "not a token":
                return "definitely-not-a-jwt";
        }

        return handler.CreateToken(descriptor);
    }

    /// <summary>The host's own key: valid signatures, so only the property under test makes the token invalid.</summary>
    private static SigningCredentials TestSigning()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(TestKeys.SigningKeyPem);
        return new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256);
    }

    private static SigningCredentials OtherKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
