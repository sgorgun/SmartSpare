using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.MasterData;

/// <summary>
/// The endpoints every catalogue shares (ADR 0025), for any catalogue route such as
/// <c>api/v1/inventory/adjustment-reasons</c>: list, read, lookup, create, update with If-Match, archive and restore,
/// and the history.
/// </summary>
public class CatalogClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<PagedResponse<T>>> ListAsync<T>(
        string route, string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"archived={(archived ? "true" : "false")}&page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<T>>(new HttpRequestMessage(HttpMethod.Get, $"{route}?{query}"), cancellationToken);
    }

    public virtual Task<ApiResult<T>> GetAsync<T>(string route, long id, CancellationToken cancellationToken = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"{route}/{id}")), cancellationToken);

    public virtual Task<ApiResult<List<CatalogLookupItem>>> LookupAsync(string route, string? search, CancellationToken cancellationToken = default) =>
        SendAsync<List<CatalogLookupItem>>(
            new HttpRequestMessage(HttpMethod.Get, $"{route}/lookup?limit={CatalogLimits.DefaultLookupLimit}"
                                                   + (string.IsNullOrWhiteSpace(search) ? string.Empty : "&search=" + Uri.EscapeDataString(search.Trim()))),
            cancellationToken);

    public virtual Task<ApiResult<T>> CreateAsync<TWrite, T>(string route, TWrite body, CancellationToken cancellationToken = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) }, cancellationToken);

    /// <summary>Changes a record against <paramref name="version"/>, the version the user saw (If-Match).</summary>
    public virtual Task<ApiResult<T>> UpdateAsync<TWrite, T>(string route, long id, TWrite body, string version, CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"{route}/{id}")) { Content = JsonContent.Create(body) };
        message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return SendAsync<T>(message, cancellationToken);
    }

    /// <summary><c>archive</c> or <c>restore</c> against the version the user saw.</summary>
    public virtual Task<ApiResult<JsonElement>> ActionAsync(string route, long id, string action, string version, CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"{route}/{id}/{action}"));
        message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return SendAsync<JsonElement>(message, cancellationToken);
    }

    public virtual Task<ApiResult<PagedResponse<RecordHistoryEntry>>> HistoryAsync(string route, long id, int page, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResponse<RecordHistoryEntry>>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture,
                $"{route}/{id}/history?page={page}&pageSize={CatalogLimits.DefaultHistoryPageSize}")),
            cancellationToken);
}
