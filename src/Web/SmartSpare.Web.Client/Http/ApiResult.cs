using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSpare.Web.Client.Http;

/// <summary>
/// The parts of an RFC 9457 ProblemDetails the client shows: the stable <c>code</c> (SS1xx–SS4xx, SS-AUTH-xxx) and the
/// W3C <c>traceId</c> the user can quote (#60, #72), and the <c>reasons</c> of an archive refusal (SS501, ADR 0025).
/// </summary>
public sealed record ProblemInfo(int Status, string? Code, string? Title, string? Detail, string? TraceId, IReadOnlyList<string>? Reasons = null)
{
    public const string MediaType = "application/problem+json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the problem of an error response; a response without one becomes a problem with its status only.</summary>
    public static async Task<ProblemInfo> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        if (response.Content.Headers.ContentType?.MediaType != MediaType)
        {
            return new ProblemInfo(status, null, null, null, null);
        }

        try
        {
            var body = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, cancellationToken).ConfigureAwait(false);
            return new ProblemInfo(body?.Status ?? status, body?.Code, body?.Title, body?.Detail, body?.TraceId, body?.Reasons);
        }
        catch (JsonException)
        {
            return new ProblemInfo(status, null, null, null, null);
        }
    }

    private sealed record ProblemBody(
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("traceId")] string? TraceId,
        [property: JsonPropertyName("reasons")] string[]? Reasons);
}

/// <summary>Result of an API call: the value on success, otherwise the problem (STAGE_3_PLAN section 8.5).</summary>
public sealed class ApiResult<T>
{
    internal ApiResult(T? value, ProblemInfo? problem)
    {
        Value = value;
        Problem = problem;
    }

    public bool IsSuccess => Problem is null;

    public T? Value { get; }

    public ProblemInfo? Problem { get; }
}

/// <summary>Creates <see cref="ApiResult{T}"/> values.</summary>
public static class ApiResult
{
    public static ApiResult<T> Success<T>(T value) => new(value, null);

    public static ApiResult<T> Failure<T>(ProblemInfo problem) => new(default, problem ?? throw new ArgumentNullException(nameof(problem)));
}

/// <summary>An empty success value for calls that return 204.</summary>
public readonly record struct NoContent;

/// <summary>
/// Base of the typed API clients, one per module (STAGE_3_PLAN 8.5). Same origin, so the HttpOnly cookie travels by
/// itself; the handler chain adds the antiforgery header and handles 401/403.
/// </summary>
public abstract class ApiClientBase(HttpClient http) : IDisposable
{
    protected HttpClient Http { get; } = http;

    protected async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = request;
        using var response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<T>(await ProblemInfo.ReadAsync(response, cancellationToken).ConfigureAwait(false));
        }

        if (typeof(T) == typeof(NoContent) || response.StatusCode == HttpStatusCode.NoContent)
        {
            return ApiResult.Success<T>(default!);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        return value is null
            ? ApiResult.Failure<T>(new ProblemInfo((int)response.StatusCode, null, null, null, null))
            : ApiResult.Success(value);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Http.Dispose();
        }
    }
}
