namespace SmartSpare.Modules.Identity.Domain;

/// <summary>When repeated failed sign-ins lock an account, and for how long (STAGE_3_PLAN section 2.4).</summary>
internal sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration)
{
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}
