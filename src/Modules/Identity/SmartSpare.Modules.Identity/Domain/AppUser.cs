using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// A dealership employee who signs in to SmartSpare (aggregate root of the Identity module, ADR 0013).
/// State changes only through behaviour methods; the invariants mirror the CHECK constraints of
/// <c>iam.app_user</c>, which remain the last line of defence.
/// </summary>
internal sealed partial class AppUser : AggregateRoot<Guid>
{
    /// <summary>Upper bound of <c>failed_login_count</c> (<c>ck_app_user_failed_logins</c>).</summary>
    public const short MaxFailedLoginCount = 100;

    private readonly List<UserRoleAssignment> _roles = [];
    private readonly List<UserBrandAssignment> _brands = [];

    private AppUser()
        : base(Guid.Empty)
    {
        UserName = null!;
        Email = null!;
        FullName = null!;
    }

    private AppUser(Guid id, UserName userName, EmailAddress email, string fullName, string? phone, string passwordHash, short? homeWarehouseId)
        : base(id)
    {
        UserName = userName;
        Email = email;
        FullName = fullName;
        Phone = phone;
        PasswordHash = passwordHash;
        HomeWarehouseId = homeWarehouseId;
        SecurityStamp = Guid.NewGuid();
        IsActive = true;
    }

    public UserName UserName { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public EmailAddress Email { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public string FullName { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public string? Phone { get; private set; }

    /// <summary>ASP.NET Core Identity V3 hash; <see langword="null"/> only for deactivated or anonymised users.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>Changes whenever issued sessions must be re-validated (password, roles, brands, deactivation).</summary>
    public Guid SecurityStamp { get; private set; }

    public bool IsActive { get; private set; }

    public short FailedLoginCount { get; private set; }

    public DateTimeOffset? LockoutEnd { get; private set; }

    /// <summary>Home warehouse of a warehouse keeper (an id of the Inventory module, no FK; ADR 0011).</summary>
    public short? HomeWarehouseId { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>Set by <c>iam.anonymise_user()</c> (GDPR Art. 17); the row is kept for audit references.</summary>
    public DateTimeOffset? AnonymisedAt { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Maintained by the <c>updated_at</c> trigger.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<UserRoleAssignment> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<UserBrandAssignment> Brands => _brands.AsReadOnly();

    /// <summary>Creates an active user with a password hash.</summary>
    public static AppUser Create(
        Guid id, UserName userName, EmailAddress email, string fullName, string? phone, string passwordHash, short? homeWarehouseId = null)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        EnsureHashFormat(passwordHash);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A user needs an identifier.", nameof(id));
        }

        return new AppUser(id, userName, email, fullName, phone, passwordHash, homeWarehouseId);
    }

    /// <summary>
    /// A sign-in succeeded: remember the time. Refused for an inactive user. The failure counter is reset by
    /// ASP.NET Core Identity through <see cref="ResetFailedSignIns"/>, which owns the lockout algorithm.
    /// </summary>
    public Result RecordSuccessfulSignIn(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return IdentityErrors.UserInactive;
        }

        LastLoginAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Counts one failed sign-in and returns the new count. ASP.NET Core Identity decides when the count reaches
    /// <see cref="LockoutPolicy.MaxFailedAttempts"/>, locks the account (<see cref="SetLockoutEnd"/>) and resets the
    /// counter (<see cref="ResetFailedSignIns"/>), so the count never approaches the CHECK's limit; the guard here
    /// only protects that invariant.
    /// </summary>
    public short RecordFailedSignIn()
    {
        if (FailedLoginCount >= MaxFailedLoginCount)
        {
            throw new InvalidOperationException($"failed_login_count cannot exceed {MaxFailedLoginCount}.");
        }

        return ++FailedLoginCount;
    }

    /// <summary>Clears the failed sign-in counter (after a successful sign-in or when a lockout starts).</summary>
    public void ResetFailedSignIns() => FailedLoginCount = 0;

    /// <summary>
    /// Locks the account until <paramref name="lockoutEnd"/>; a time in the past or <see langword="null"/> unlocks it.
    /// </summary>
    public void SetLockoutEnd(DateTimeOffset? lockoutEnd) => LockoutEnd = lockoutEnd;

    /// <summary>Replaces the password hash (new password or rehash with more iterations) and rotates the stamp.</summary>
    public void ChangePasswordHash(string passwordHash)
    {
        EnsureHashFormat(passwordHash);
        PasswordHash = passwordHash;
        RotateSecurityStamp();
    }

    /// <summary>Ends a lockout after failed sign-ins (user administration, #46).</summary>
    public void Unlock()
    {
        LockoutEnd = null;
        FailedLoginCount = 0;
    }

    /// <summary>The home hub of a warehouse keeper; a change rotates the stamp, because the hub is a claim (SS302).</summary>
    public void SetHomeWarehouse(short? warehouseId)
    {
        if (HomeWarehouseId == warehouseId)
        {
            return;
        }

        HomeWarehouseId = warehouseId;
        RotateSecurityStamp();
    }

    /// <summary>Invalidates every issued session at its next check.</summary>
    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();

    /// <summary>Deactivates the user: no new sign-ins, existing sessions end at the next check.</summary>
    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        RotateSecurityStamp();
        Raise(new UserDeactivated(Id, now));
    }

    /// <summary>Grants a role. Granting a role the user already has changes nothing.</summary>
    public void AssignRole(short roleId, DateTimeOffset grantedAt, Guid? grantedBy)
    {
        if (_roles.Exists(r => r.RoleId == roleId))
        {
            return;
        }

        _roles.Add(new UserRoleAssignment(roleId, grantedAt, grantedBy));
        RotateSecurityStamp();
    }

    /// <summary>Revokes a role. Revoking a role the user doesn't have changes nothing.</summary>
    public void RevokeRole(short roleId)
    {
        if (_roles.RemoveAll(r => r.RoleId == roleId) > 0)
        {
            RotateSecurityStamp();
        }
    }

    /// <summary>Makes the user responsible for a brand (brand scope of approvals, STAGE_3_PLAN section 3.3).</summary>
    public void AssignBrand(short brandId)
    {
        if (_brands.Exists(b => b.BrandId == brandId))
        {
            return;
        }

        _brands.Add(new UserBrandAssignment(brandId));
        RotateSecurityStamp();
    }

    public void RemoveBrand(short brandId)
    {
        if (_brands.RemoveAll(b => b.BrandId == brandId) > 0)
        {
            RotateSecurityStamp();
        }
    }

    /// <summary>
    /// The hash format of <c>ck_app_user_password_hash_format</c>: an ASP.NET Core Identity V3 hash in
    /// base64 (it rejects plain text, MD5 and SHA digests).
    /// </summary>
    private static void EnsureHashFormat(string passwordHash)
    {
        if (passwordHash is null || !HashFormat().IsMatch(passwordHash))
        {
            throw new ArgumentException("The password hash is not an ASP.NET Core Identity V3 hash.", nameof(passwordHash));
        }
    }

    [GeneratedRegex("^AQAAAA[A-Za-z0-9+/]{76,}={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashFormat();
}
