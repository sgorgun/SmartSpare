using System.Globalization;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Inventory;

/// <summary>The read-only Inventory API, <c>/api/v1/inventory</c> (STAGE_3_PLAN 8.5: one typed client per module).</summary>
public class InventoryClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<PagedResponse<PartSummary>>> SearchPartsAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<PartSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/parts?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<PartDetails>> GetPartAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync<PartDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/parts/{id}")), cancellationToken);

    public virtual Task<ApiResult<IReadOnlyList<WarehouseResponse>>> ListWarehousesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<WarehouseResponse>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/warehouses"), cancellationToken);
}
