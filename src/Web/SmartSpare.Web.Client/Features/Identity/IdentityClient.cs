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
}
