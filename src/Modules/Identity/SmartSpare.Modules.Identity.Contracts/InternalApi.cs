using System.Security.Claims;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// The Identity service's internal endpoints behind <see cref="ICredentialVerifier"/> (ADR 0016 section 1,
/// STAGE_4_PLAN section 4). Only the gateway calls them, with its own service token (policy <c>GatewayOnly</c>).
/// </summary>
public static class IdentityInternalApi
{
    public const string Prefix = "/internal/v1/identity";

    public const string CredentialChecks = Prefix + "/credential-checks";

    public const string SessionChecks = Prefix + "/session-checks";

    /// <summary>Employee lookups of <see cref="IEmployeeDirectory"/> for other services (any signed-in caller, with the caller's token).</summary>
    public const string EmployeeLookup = Prefix + "/employee-lookups";
}

/// <summary>Body of <see cref="IdentityInternalApi.CredentialChecks"/>.</summary>
public sealed record CredentialCheckRequest(string UserName, string Password, string? ClientIp);

/// <summary>Body of <see cref="IdentityInternalApi.SessionChecks"/>.</summary>
public sealed record SessionCheckRequest(Guid UserId, string SecurityStamp);

/// <summary>Answer of both checks when they succeed: the session's claims as type and value pairs.</summary>
public sealed record VerifiedSessionResponse(Guid UserId, IReadOnlyList<ClaimValue> Claims)
{
    public static VerifiedSessionResponse From(VerifiedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(session.UserId, [.. session.Claims.Select(c => new ClaimValue(c.Type, c.Value))]);
    }

    public VerifiedSession ToSession() => new(UserId, [.. Claims.Select(c => new Claim(c.Type, c.Value))]);
}

public sealed record ClaimValue(string Type, string Value);
