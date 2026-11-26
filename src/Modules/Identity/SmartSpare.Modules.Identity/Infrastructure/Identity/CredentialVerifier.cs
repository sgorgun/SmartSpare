using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>
/// In-process <see cref="ICredentialVerifier"/> of the single host (ADR 0016 section 1). Sign-in runs
/// <c>SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)</c>: password check, lockout, the active-user
/// confirmation and the rehash with the current work factor. A success also records <c>last_login_at</c>. All writes
/// run in one transaction attributed to the user who is signing in, with the reason <c>sign-in</c>. It never issues a
/// cookie; the BFF does.
/// </summary>
internal sealed partial class CredentialVerifier(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IPasswordHasher<AppUser> passwordHasher,
    IdentityDbContext db,
    ActingUser actingUser,
    TimeProvider time,
    ILogger<CredentialVerifier> logger) : ICredentialVerifier
{
    /// <summary>Audit reason of every write during sign-in (STAGE_3_PLAN section 2.2).</summary>
    public const string SignInReason = "sign-in";

    private static readonly Lock DummyHashLock = new();
    private static string? s_dummyHash;

    public async Task<Result<VerifiedSession>> VerifyAsync(
        string userName, string password, IPAddress? clientIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        var user = string.IsNullOrWhiteSpace(userName) ? null : await users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            // Spend the same PBKDF2 work as for a real account, so the response time doesn't reveal which names exist.
            passwordHasher.VerifyHashedPassword(null!, DummyHash(), password);
            LogUnknownUser(logger);
            return IdentityContractErrors.InvalidCredentials;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                using var acting = actingUser.ActAs(user.Id, SignInReason);
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);   // keeps the failure count and lockout
                    LogRefused(user.Id, result.IsLockedOut, result.IsNotAllowed);
                    return Result.Failure<VerifiedSession>(IdentityContractErrors.InvalidCredentials);
                }

                if (user.RecordSuccessfulSignIn(time.GetUtcNow()).IsFailure)
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    return Result.Failure<VerifiedSession>(IdentityContractErrors.InvalidCredentials);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                var session = await CreateSessionAsync(user).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Result.Success(session);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<VerifiedSession>> ValidateSessionAsync(Guid userId, string securityStamp, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null
            || !await signIn.ValidateSecurityStampAsync(user, securityStamp).ConfigureAwait(false)
            || !await signIn.CanSignInAsync(user).ConfigureAwait(false))
        {
            return IdentityContractErrors.SessionRevoked;
        }

        return await CreateSessionAsync(user).ConfigureAwait(false);
    }

    private async Task<VerifiedSession> CreateSessionAsync(AppUser user)
    {
        var principal = await claimsFactory.CreateAsync(user).ConfigureAwait(false);
        return new VerifiedSession(user.Id, [.. principal.Claims]);
    }

    private string DummyHash()
    {
        lock (DummyHashLock)
        {
            // One hash per process from the injected hasher (same algorithm and cost as real users), under the lock.
#pragma warning disable S2696
            return s_dummyHash ??= passwordHasher.HashPassword(null!, Guid.NewGuid().ToString("N"));
#pragma warning restore S2696
        }
    }

    private void LogRefused(Guid userId, bool lockedOut, bool notAllowed)
    {
        if (lockedOut)
        {
            LogLockedOut(logger, userId);
        }
        else if (notAllowed)
        {
            LogNotAllowed(logger, userId);
        }
        else
        {
            LogWrongPassword(logger, userId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused: unknown user name")]
    private static partial void LogUnknownUser(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused for user {UserId}: wrong password")]
    private static partial void LogWrongPassword(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in refused for user {UserId}: the account is locked out")]
    private static partial void LogLockedOut(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused for user {UserId}: the user is inactive or anonymised")]
    private static partial void LogNotAllowed(ILogger logger, Guid userId);
}
