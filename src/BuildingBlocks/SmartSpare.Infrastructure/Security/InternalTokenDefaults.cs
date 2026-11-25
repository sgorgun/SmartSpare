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

    /// <summary>
    /// Claim that marks a service's own token (ADR 0016 section 1): the gateway calls the Identity service's credential
    /// and session checks as itself, not as a user (<c>sub=gateway</c>, <c>ss:svc=gateway</c>).
    /// </summary>
    public const string ServiceClaim = "ss:svc";

    public const string GatewayService = "gateway";

    /// <summary>Policy of the endpoints only the gateway may call, such as <c>/internal/v1/identity/*</c>.</summary>
    public const string GatewayOnlyPolicy = "GatewayOnly";
}
