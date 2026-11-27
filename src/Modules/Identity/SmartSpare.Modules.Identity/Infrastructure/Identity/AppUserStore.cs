using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity store over the existing <c>iam</c> tables (STAGE_3_PLAN section 2, ADR 0014). It translates
/// Identity's calls into the behaviour methods of the <see cref="AppUser"/> aggregate; Identity's managers run the
/// algorithms (password check, rehash, lockout). Read-only for users and roles in stage 3: creating users and changing
/// roles is user administration (#46).
/// </summary>
internal sealed class AppUserStore(IdentityDbContext db, OrganisationReader organisation) :
    IUserPasswordStore<AppUser>,
    IUserSecurityStampStore<AppUser>,
    IUserLockoutStore<AppUser>,
    IUserRoleStore<AppUser>
{
    private const string NotSupportedInStage3 = "User administration is not available yet (#46).";

    private IQueryable<AppUser> Users => db.Set<AppUser>();

    public void Dispose()
    {
        // The DbContext belongs to the DI scope.
    }

    // ---- IUserStore -------------------------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(NotNull(user).Id.ToString());

    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(NotNull(user).UserName.Value);

    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    /// <summary>The stored name is the canonical lower-case form, which is also the normalized form.</summary>
    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(NotNull(user).UserName.Value);

    /// <summary>No normalized column: <c>user_name</c> is <c>citext</c> and stored lower-case (section 2.2).</summary>
    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    public async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken)
    {
        NotNull(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
        }
    }

    public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id)
            ? Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
            : Task.FromResult<AppUser?>(null);

    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        // A name that cannot be valid cannot exist (ck_app_user_user_name); the comparison runs on citext.
        var name = UserName.Create(normalizedUserName);
        return name.IsSuccess
            ? Users.SingleOrDefaultAsync(u => u.UserName == name.Value, cancellationToken)
            : Task.FromResult<AppUser?>(null);
    }

    // ---- IUserPasswordStore -----------------------------------------------------------------------------------

    public Task SetPasswordHashAsync(AppUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (passwordHash is null)
        {
            throw new NotSupportedException("Removing a password is not supported; deactivate the user instead.");
        }

        NotNull(user).ChangePasswordHash(passwordHash);
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(NotNull(user).PasswordHash);

    public Task<bool> HasPasswordAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(NotNull(user).PasswordHash is not null);

    // ---- IUserSecurityStampStore --------------------------------------------------------------------------------

    /// <summary>
    /// <c>security_stamp</c> is a uuid, while Identity proposes Base32 strings. The proposal is ignored and the
    /// aggregate stores a new Guid: the stamp only has to change (section 2.5).
    /// </summary>
    public Task SetSecurityStampAsync(AppUser user, string stamp, CancellationToken cancellationToken)
    {
        NotNull(user).RotateSecurityStamp();
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(StampOf(NotNull(user)));

    // ---- IUserLockoutStore (Identity drives the algorithm; the aggregate keeps the state) -----------------------

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(NotNull(user).LockoutEnd);

    public Task SetLockoutEndDateAsync(AppUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        NotNull(user).SetLockoutEnd(lockoutEnd);
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult((int)NotNull(user).RecordFailedSignIn());

    public Task ResetAccessFailedCountAsync(AppUser user, CancellationToken cancellationToken)
    {
        NotNull(user).ResetFailedSignIns();
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult((int)NotNull(user).FailedLoginCount);

    /// <summary>Lockout applies to every user; there is no column for the flag (section 2.2).</summary>
    public Task<bool> GetLockoutEnabledAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(AppUser user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

    // ---- IUserRoleStore (read-only) -----------------------------------------------------------------------------

    /// <summary>The roles of the employee's position (ADR 0026); <c>iam.user_role</c> is only a shadow until PR 7e.</summary>
    public async Task<IList<string>> GetRolesAsync(AppUser user, CancellationToken cancellationToken) =>
        (await organisation.RightsForClaimsAsync(NotNull(user), cancellationToken).ConfigureAwait(false)).Roles.ToList();

    public async Task<bool> IsInRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) =>
        (await GetRolesAsync(user, cancellationToken).ConfigureAwait(false)).Contains(roleName, StringComparer.Ordinal);

    public Task AddToRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    public Task RemoveFromRoleAsync(AppUser user, string roleName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    public Task<IList<AppUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotSupportedInStage3);

    /// <summary>The stamp in the text form used in claims and session checks.</summary>
    public static string StampOf(AppUser user) => user.SecurityStamp.ToString("N");

    private static AppUser NotNull(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user;
    }
}
