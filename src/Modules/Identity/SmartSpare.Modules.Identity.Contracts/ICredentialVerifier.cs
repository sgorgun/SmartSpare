using System.Net;
using System.Security.Claims;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// Checks credentials and sessions for the BFF (ADR 0016 section 1). The BFF issues and validates the cookie but
/// never touches <c>iam</c>; the Identity module answers. In the single host this is an in-process implementation;
/// behind the gateway (stage 4) an HTTP client calls the Identity service's internal endpoints.
/// </summary>
public interface ICredentialVerifier
{
    /// <summary>
    /// Checks a user name and password: password, lockout, active and not anonymised, rehash with the current work
    /// factor, last sign-in time. Every failure is the same <see cref="IdentityContractErrors.InvalidCredentials"/>,
    /// so the result never tells which check failed.
    /// </summary>
    /// <returns>The claims of the session to issue, including the security stamp claim.</returns>
    Task<Result<VerifiedSession>> VerifyAsync(string userName, string password, IPAddress? clientIp, CancellationToken cancellationToken);

    /// <summary>
    /// Re-validates an issued session: the user still exists, may sign in and has the same security stamp.
    /// </summary>
    /// <returns>The current claims (roles and brands may have changed), or <see cref="IdentityContractErrors.SessionRevoked"/>.</returns>
    Task<Result<VerifiedSession>> ValidateSessionAsync(Guid userId, string securityStamp, CancellationToken cancellationToken);
}

/// <summary>The claims of a verified session, in the claim types of <c>AppClaimTypes</c> plus the security stamp.</summary>
public sealed record VerifiedSession(Guid UserId, IReadOnlyList<Claim> Claims);

/// <summary>Claim types the Identity module adds besides the application claims.</summary>
public static class IdentityClaimTypes
{
    /// <summary>Security stamp of the session; checked by the BFF every 5 minutes and never sent to the browser.</summary>
    public const string SecurityStamp = "ss:stamp";
}

/// <summary>Errors of <see cref="ICredentialVerifier"/>.</summary>
public static class IdentityContractErrors
{
    public static readonly Error InvalidCredentials = Error.Validation("Identity.InvalidCredentials", "The user name or password is incorrect.");

    public static readonly Error SessionRevoked = Error.Forbidden("Identity.SessionRevoked", "The session is no longer valid.");
}
