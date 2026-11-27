namespace SmartSpare.Bff;

/// <summary>
/// How often an open session is re-validated (ADR 0026 decision 4): a rights change or an employee leaving reaches open
/// sessions within this window; sign-in is refused at once anyway. Section <c>Bff:Session</c>.
/// </summary>
public sealed class SessionCheckOptions
{
    public const string Section = "Bff:Session";

    /// <summary>At most this long between session checks; default 30 seconds (one indexed query per active session).</summary>
    public TimeSpan CheckInterval { get; set; } = BffDefaults.SessionCheckInterval;
}
