using SmartSpare.ApiContracts;
using System.Net.Http.Json;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Identity;

/// <summary>The <c>/bff</c> endpoints (STAGE_3_PLAN 1.2): sign-in, sign-out and the current user.</summary>
public class IdentityClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<NoContent>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<NoContent>(
            new HttpRequestMessage(HttpMethod.Post, "bff/login") { Content = JsonContent.Create(request) }, cancellationToken);
    }

    public virtual Task<ApiResult<NoContent>> LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NoContent>(new HttpRequestMessage(HttpMethod.Post, "bff/logout"), cancellationToken);

    public virtual Task<ApiResult<CurrentUserResponse>> MeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<CurrentUserResponse>(new HttpRequestMessage(HttpMethod.Get, "bff/me"), cancellationToken);

    public virtual Task<ApiResult<PagedResponse<UserSummary>>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<UserSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/users?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<UserFormOptions>> GetUserFormOptionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<UserFormOptions>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/users/form-options"), cancellationToken);

    public virtual Task<ApiResult<UserSummary>> UpdateUserAccessAsync(Guid id, UserAccessRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(
            new HttpRequestMessage(HttpMethod.Put, $"api/v1/identity/users/{id}/access") { Content = System.Net.Http.Json.JsonContent.Create(request) },
            cancellationToken);

    /// <summary>unlock, deactivate or anonymise.</summary>
    public virtual Task<ApiResult<UserSummary>> UserActionAsync(Guid id, string action, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/users/{id}/{action}"), cancellationToken);

    public virtual Task<ApiResult<NoContent>> ChangeOwnPasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NoContent>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/identity/me/password") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);
}
