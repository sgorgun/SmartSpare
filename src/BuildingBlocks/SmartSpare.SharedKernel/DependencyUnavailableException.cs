namespace SmartSpare.SharedKernel;

/// <summary>
/// Another service this request depends on could not answer: unreachable, timed out, or an unexpected response
/// (ADR 0015 §4, STAGE_4_PLAN section 4). Hosts answer 503 with the trace id; it is never turned into a business
/// result such as "wrong password".
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException()
    {
    }

    public DependencyUnavailableException(string message)
        : base(message)
    {
    }

    public DependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The service that did not answer, such as <c>identity</c>.</summary>
    public string? Service { get; init; }
}
