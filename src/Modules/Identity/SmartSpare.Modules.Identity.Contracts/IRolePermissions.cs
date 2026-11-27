using System.Net.Http.Json;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// The permissions of every role (ADR 0028), for the BFF, which adds a user's permissions to the request from the role
/// codes of the session (they never go into the cookie). In the single host the Identity module answers in process;
/// the gateway uses <see cref="HttpRolePermissions"/>. Roles are data, so the map comes from the database.
/// </summary>
public interface IRolePermissions
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IRolePermissions"/> over HTTP to the Identity service; a transport failure is a 503, never "no rights".</summary>
public sealed class HttpRolePermissions(HttpClient client) : IRolePermissions
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFromJsonAsync<Dictionary<string, IReadOnlyList<string>>>(
                       new Uri(IdentityInternalApi.RolePermissions, UriKind.Relative), cancellationToken).ConfigureAwait(false)
                   ?? throw new DependencyUnavailableException("The Identity service answered the role permissions without a body.") { Service = "identity" };
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DependencyUnavailableException("The Identity service did not answer the role permissions.", ex) { Service = "identity" };
        }
    }
}
