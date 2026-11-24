namespace SmartSpare.Infrastructure.Security;

/// <summary>
/// Fixed properties of the internal token that carries the user from the gateway to the services (ADR 0016
/// section 2): ES256, issuer <c>smartspare-gateway</c>, audience <c>smartspare-internal</c>, 2-minute lifetime.
/// </summary>
public static class InternalTokenDefaults
{
    /// <summary>Name of the JwtBearer scheme that validates internal tokens.</summary>
    public const string Scheme = "SmartSpare.InternalToken";

    /// <summary>
    /// Authentication type of a principal built from an internal token. The antiforgery filter recognises bearer
    /// requests by it.
    /// </summary>
    public const string AuthenticationType = "SmartSpare.InternalToken";

    public const string Issuer = "smartspare-gateway";

    public const string Audience = "smartspare-internal";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
}
