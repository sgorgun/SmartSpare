using System.Globalization;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Inventory;

/// <summary>The Inventory API, <c>/api/v1/inventory</c> (STAGE_3_PLAN 8.5: one typed client per module).</summary>
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

    public virtual Task<ApiResult<PagedResponse<StockRow>>> GetStockAsync(
        short? warehouseId, string? status, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (warehouseId is { } warehouse)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&warehouseId={warehouse}");
        }

        if (!string.IsNullOrEmpty(status))
        {
            query += "&status=" + Uri.EscapeDataString(status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<StockRow>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/stock?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<StockPositionDetails>> GetStockPositionAsync(short warehouseId, long partId, CancellationToken cancellationToken = default) =>
        SendAsync<StockPositionDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/stock/{warehouseId}/{partId}")),
            cancellationToken);

    /// <summary>Changes the stock levels against <paramref name="version"/>, the version the user saw (If-Match).</summary>
    public virtual Task<ApiResult<StockRow>> ChangeStockLevelsAsync(
        short warehouseId, long partId, ChangeStockLevelsRequest request, string version, CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/stock/{warehouseId}/{partId}/levels"))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return SendAsync<StockRow>(message, cancellationToken);
    }

    public virtual Task<ApiResult<StockAdjustmentResponse>> AdjustStockAsync(StockAdjustmentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<StockAdjustmentResponse>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/stock-adjustments") { Content = JsonContent.Create(request) }, cancellationToken);
}
