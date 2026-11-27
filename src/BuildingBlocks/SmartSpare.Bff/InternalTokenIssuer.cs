using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Bff;

/// <summary>
/// Mints the internal token that carries the signed-in user to the services (ADR 0016 section 2): a JWS signed with
/// ES256 by <see cref="JsonWebTokenHandler"/>, <c>iss=smartspare-gateway</c>, <c>aud=smartspare-internal</c>, the claims of
/// STAGE_3_PLAN section 3.1 plus <c>sid</c> and <c>jti</c>, valid for 2 minutes. The security stamp is not forwarded.
/// </summary>
public sealed class InternalTokenIssuer(InternalTokenKeyRing keys, TimeProvider time)
{
    private static readonly HashSet<string> ForwardedClaimTypes =
        new(StringComparer.Ordinal) { AppClaimTypes.Subject, AppClaimTypes.Name, AppClaimTypes.Role, AppClaimTypes.Brand, AppClaimTypes.Warehouse };

    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Whether this host holds the signing key.</summary>
    public bool CanIssue => keys.SigningKey is not null;

    /// <summary>
    /// A token for a service acting as itself (ADR 0016 section 1): <c>sub</c> and <c>ss:svc</c> are the service name, no
    /// user claims. The gateway uses it for the Identity service's credential and session checks.
    /// </summary>
    public string CreateServiceToken(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        var key = keys.SigningKey ?? throw new InvalidOperationException("InternalTokens:SigningKey is not configured.");
        var now = time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(AppClaimTypes.Subject, service),
                new Claim(InternalTokenDefaults.ServiceClaim, service),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + InternalTokenDefaults.Lifetime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public string CreateToken(ClaimsPrincipal user, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var key = keys.SigningKey ?? throw new InvalidOperationException("InternalTokens:SigningKey is not configured.");

        var claims = user.Claims
            .Where(c => ForwardedClaimTypes.Contains(c.Type))
            .Select(c => new Claim(c.Type, c.Value))
            .Append(new Claim(AppClaimTypes.SessionId, sessionId))
            // The permissions as a compact bitset with its catalogue version (ADR 0028), not as names.
            .Append(new Claim(AppClaimTypes.PermissionSet, PermissionBitset.Encode(user.FindAll(AppClaimTypes.Permission).Select(c => c.Value))))
            .Append(new Claim(AppClaimTypes.PermissionVersion, Permissions.CatalogueVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));

        var now = time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + InternalTokenDefaults.Lifetime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
        });
    }
}
