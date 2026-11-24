namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// When repeated failed sign-ins lock an account, and for how long (STAGE_3_PLAN section 2.4). The one source of these
/// values: the Identity installer copies <see cref="Default"/> into <c>IdentityOptions.Lockout</c>, and ASP.NET Core
/// Identity runs the lockout algorithm with them.
/// </summary>
internal sealed record LockoutPolicy
{
    public LockoutPolicy(int maxFailedAttempts, TimeSpan duration)
    {
        // Identity resets the counter when it locks the account, so the count stays below MaxFailedAttempts,
        // which therefore must not exceed the CHECK's limit of failed_login_count.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFailedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFailedAttempts, AppUser.MaxFailedLoginCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        MaxFailedAttempts = maxFailedAttempts;
        Duration = duration;
    }

    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));

    public int MaxFailedAttempts { get; }

    public TimeSpan Duration { get; }
}
