namespace SmartSpare.Bff;

/// <summary>Names and limits of the BFF (STAGE_3_PLAN sections 1.3–1.5 and 4).</summary>
public static class BffDefaults
{
    /// <summary>Default scheme of the single host: forwards to the internal token or the cookie (ADR 0016 section 4).</summary>
    public const string PolicyScheme = "SmartSpare";

    public const string CookieScheme = "SmartSpare.Cookie";

    /// <summary>The <c>__Host-</c> prefix makes the browser require Secure, <c>Path=/</c> and no Domain.</summary>
    public const string CookieName = "__Host-SmartSpare";

    public const string AntiforgeryCookieName = "__Host-SmartSpare-AF";

    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>Name of the rate-limit policy of <c>POST /bff/login</c>.</summary>
    public const string LoginRateLimitPolicy = "login";

    public const int LoginPermitsPerWindow = 10;

    public static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);

    /// <summary>One work shift; the expiry slides.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    /// <summary>How often the session is re-validated through <c>ICredentialVerifier</c> (section 2.5).</summary>
    public static readonly TimeSpan SessionCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>Key of the session id in the cookie's authentication properties (carried into internal tokens as <c>sid</c>).</summary>
    public const string SessionIdItem = ".ss.sid";

    /// <summary>Key of the time of the last session check in the cookie's authentication properties.</summary>
    public const string ValidatedAtItem = ".ss.validated";
}
