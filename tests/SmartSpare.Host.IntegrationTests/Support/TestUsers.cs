using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>An in-memory ES256 key for the tests; never written to a file.</summary>
internal static class TestKeys
{
    public static string SigningKeyPem { get; } = NewPem();

    public static string NewPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}

/// <summary>A user of the fake verifier: the claims the Identity module would return.</summary>
internal sealed record TestUser(Guid Id, string UserName, string Password, IReadOnlyList<string> Roles, IReadOnlyList<short> Brands, short? Warehouse = null)
{
    public string Stamp { get; set; } = Guid.NewGuid().ToString("N");

    public IReadOnlyList<string> CurrentRoles { get; set; } = Roles;

    public bool Revoked { get; set; }

    public static TestUser Create(string userName, string[] roles, short[]? brands = null, short? warehouse = null) =>
        new(Guid.NewGuid(), userName, "correct horse battery staple", roles, brands ?? [], warehouse);

    public List<Claim> Claims() => ClaimsFor(Id, UserName, CurrentRoles, Brands, Warehouse, Stamp);

    public static List<Claim> ClaimsFor(Guid id, string name, IEnumerable<string> roles, IEnumerable<short> brands, short? warehouse, string? stamp = null)
    {
        var claims = new List<Claim> { new(AppClaimTypes.Subject, id.ToString()), new(AppClaimTypes.Name, name) };
        if (stamp is not null)
        {
            claims.Add(new Claim(IdentityClaimTypes.SecurityStamp, stamp));
        }

        claims.AddRange(roles.Select(r => new Claim(AppClaimTypes.Role, r)));
        claims.AddRange(brands.Select(b => new Claim(AppClaimTypes.Brand, b.ToString(CultureInfo.InvariantCulture))));
        if (warehouse is { } w)
        {
            claims.Add(new Claim(AppClaimTypes.Warehouse, w.ToString(CultureInfo.InvariantCulture)));
        }

        return claims;
    }

    public ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(ClaimsFor(Id, UserName, CurrentRoles, Brands, Warehouse), "test", AppClaimTypes.Name, AppClaimTypes.Role));
}

/// <summary>
/// <see cref="ICredentialVerifier"/> without a database: proves that the BFF needs nothing but the contract (ADR 0016),
/// which is how the gateway will use it in stage 4.
/// </summary>
internal sealed class FakeCredentialVerifier : ICredentialVerifier
{
    private readonly Dictionary<string, TestUser> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public TestUser Add(TestUser user)
    {
        lock (_lock)
        {
            _users[user.UserName] = user;
        }

        return user;
    }

    public int SessionChecks { get; private set; }

    public Task<Result<VerifiedSession>> VerifyAsync(string userName, string password, IPAddress? clientIp, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.TryGetValue(userName, out var user) && user.Password == password && !user.Revoked
                ? Result.Success(new VerifiedSession(user.Id, user.Claims()))
                : Result.Failure<VerifiedSession>(IdentityContractErrors.InvalidCredentials));
        }
    }

    public Task<Result<VerifiedSession>> ValidateSessionAsync(Guid userId, string securityStamp, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            SessionChecks++;
            var user = _users.Values.SingleOrDefault(u => u.Id == userId);
            return Task.FromResult(user is { Revoked: false } && user.Stamp == securityStamp
                ? Result.Success(new VerifiedSession(user.Id, user.Claims()))
                : Result.Failure<VerifiedSession>(IdentityContractErrors.SessionRevoked));
        }
    }
}
