using System.Security.Claims;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// The Identity service's internal endpoints behind <see cref="ICredentialVerifier"/> and <see cref="IRolePermissions"/>
/// (ADR 0016 section 1, STAGE_4_PLAN section 4). Only the gateway calls them; they are the only synchronous calls between
/// services (ADR 0029 amendment 2).
/// </summary>
public static class IdentityInternalApi
{
    public const string Prefix = "/internal/v1/identity";

    public const string CredentialChecks = Prefix + "/credential-checks";

    public const string SessionChecks = Prefix + "/session-checks";

    /// <summary>GET: role code → permissions of every role (<c>IRolePermissions</c>, ADR 0028), for the gateway.</summary>
    public const string RolePermissions = Prefix + "/role-permissions";
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
