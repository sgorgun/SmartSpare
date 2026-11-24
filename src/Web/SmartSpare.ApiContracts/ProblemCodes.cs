namespace SmartSpare.ApiContracts;

/// <summary>
/// Stable <c>code</c> values of ProblemDetails responses for authentication and session errors. Business errors
/// use the database codes SS100–SS402 and the module error codes.
/// </summary>
public static class ProblemCodes
{
    /// <summary>Sign-in failed. The same for every cause (unknown user, wrong password, locked out, inactive).</summary>
    public const string InvalidCredentials = "SS-AUTH-001";

    /// <summary>The antiforgery token is missing or invalid on an unsafe request.</summary>
    public const string AntiforgeryFailed = "SS-AUTH-002";

    /// <summary>401: no valid session (anonymous, expired or revoked).</summary>
    public const string Unauthenticated = "SS-AUTH-003";

    /// <summary>403: signed in, but the policy or the brand scope does not allow the action.</summary>
    public const string Forbidden = "SS-AUTH-004";

    /// <summary>429: too many sign-in attempts from this client; see <c>Retry-After</c>.</summary>
    public const string TooManyRequests = "SS-AUTH-005";
}
