namespace SmartSpare.Bff;

/// <summary>
/// The fixed-window limit of <c>POST /bff/login</c> per client IP (STAGE_3_PLAN section 4), section
/// <c>Bff:LoginRateLimit</c>. The defaults are the production values; only the end-to-end tests raise the limit, because
/// all their browsers sign in from one loopback address.
/// </summary>
public sealed class LoginRateLimitOptions
{
    public const string Section = "Bff:LoginRateLimit";

    public int PermitLimit { get; set; } = BffDefaults.LoginPermitsPerWindow;

    public TimeSpan Window { get; set; } = BffDefaults.LoginWindow;
}
