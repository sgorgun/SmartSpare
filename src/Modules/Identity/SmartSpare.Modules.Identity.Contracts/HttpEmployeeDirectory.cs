using System.Net.Http.Json;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// <see cref="IEmployeeDirectory"/> over HTTP for another service (ADR 0015 §4). The host registers it as a typed client
/// whose handlers forward the caller's internal token. Transport failures throw <see cref="DependencyUnavailableException"/>
/// (503), never a wrong answer.
/// </summary>
public sealed class HttpEmployeeDirectory(HttpClient client) : IEmployeeDirectory
{
    public Task<IReadOnlyList<EmployeeInfo>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? Task.FromResult<IReadOnlyList<EmployeeInfo>>([]) : LookupAsync(new EmployeeLookupRequest([.. ids], null, ids.Count), cancellationToken);

    public Task<IReadOnlyList<EmployeeInfo>> SearchAsync(string? search, int limit, CancellationToken cancellationToken) =>
        LookupAsync(new EmployeeLookupRequest([], search, limit), cancellationToken);

    private async Task<IReadOnlyList<EmployeeInfo>> LookupAsync(EmployeeLookupRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(new Uri(IdentityInternalApi.EmployeeLookup, UriKind.Relative), request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<EmployeeInfo>>(cancellationToken).ConfigureAwait(false)
                ?? throw Unavailable("The Identity service answered the employee lookup without a body.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                                   || ex.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true)
        {
            throw Unavailable("The Identity service did not answer the employee lookup.", ex);
        }
    }

    private static DependencyUnavailableException Unavailable(string message, Exception? inner = null) =>
        inner is null ? new(message) { Service = "identity" } : new(message, inner) { Service = "identity" };
}
