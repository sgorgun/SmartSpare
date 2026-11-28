using System.Globalization;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Inventory;

/// <summary>The Inventory API, <c>/api/v1/inventory</c> (STAGE_3_PLAN 8.5: one typed client per module).</summary>
public class InventoryClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<PagedResponse<PartDto>>> SearchPartsAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<PartDto>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/parts?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<PartDetails>> GetPartAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync<PartDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/parts/{id}")), cancellationToken);

    // ---- Responsible employees of brands and warehouses (ADR 0026) ---------------------------------------------------

    /// <summary>The active brands by code (the brand catalogue's first page of 100; a dealer has a few dozen brands).</summary>
    public virtual async Task<ApiResult<IReadOnlyList<BrandDto>>> ListBrandsAsync(CancellationToken cancellationToken = default)
    {
        var page = await SendAsync<PagedResponse<BrandDto>>(
            new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/brands?pageSize={SmartSpare.ApiContracts.MasterData.CatalogLimits.MaxPageSize}"), cancellationToken).ConfigureAwait(false);
        return page.IsSuccess ? ApiResult.Success<IReadOnlyList<BrandDto>>(page.Value!.Items) : ApiResult.Failure<IReadOnlyList<BrandDto>>(page.Problem!);
    }

    public virtual Task<ApiResult<IReadOnlyList<AssignmentRoleDto>>> ListAssignmentRolesAsync(string kind, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<AssignmentRoleDto>>(
            new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/assignment-roles?kind=" + Uri.EscapeDataString(kind)), cancellationToken);

    /// <summary><paramref name="segment"/> is <c>brands</c> or <c>warehouses</c>.</summary>
    public virtual Task<ApiResult<IReadOnlyList<ResponsibleDto>>> GetResponsiblesAsync(string segment, short id, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ResponsibleDto>>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/{segment}/{id}/responsibles")), cancellationToken);

    public virtual Task<ApiResult<IReadOnlyList<ResponsibleDto>>> ReplaceResponsiblesAsync(
        string segment, short id, ResponsiblesRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ResponsibleDto>>(
            new HttpRequestMessage(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/{segment}/{id}/responsibles"))
            {
                Content = JsonContent.Create(request),
            },
            cancellationToken);

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

    /// <summary>Active adjustment reasons for the adjustment form; the detail of each entry is its direction (IN, OUT, BOTH).</summary>
    public virtual Task<ApiResult<List<CatalogLookupItem>>> LookupAdjustmentReasonsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<CatalogLookupItem>>(
            new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/adjustment-reasons/lookup?limit=50"), cancellationToken);

    public virtual Task<ApiResult<StockAdjustmentResponse>> AdjustStockAsync(StockAdjustmentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<StockAdjustmentResponse>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/stock-adjustments") { Content = JsonContent.Create(request) }, cancellationToken);

    public virtual Task<ApiResult<PagedResponse<TransferSummary>>> ListTransfersAsync(
        string? status, short? warehouseId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrEmpty(status))
        {
            query += "&status=" + Uri.EscapeDataString(status);
        }

        if (warehouseId is { } warehouse)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&warehouseId={warehouse}");
        }

        return SendAsync<PagedResponse<TransferSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/transfers?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<TransferDetails>> GetTransferAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync<TransferDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/transfers/{id}")), cancellationToken);

    public virtual Task<ApiResult<TransferDetails>> CreateTransferAsync(CreateTransferRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TransferDetails>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/transfers") { Content = JsonContent.Create(request) }, cancellationToken);

    /// <summary>approve, dispatch or receive (no body), or cancel with <paramref name="reason"/>.</summary>
    public virtual Task<ApiResult<TransferDetails>> TransferStepAsync(long id, string action, string? reason = null, CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/transfers/{id}/{action}"));
        if (action == TransferActions.Cancel)
        {
            message.Content = JsonContent.Create(new CancelTransferRequest(reason ?? string.Empty));
        }

        return SendAsync<TransferDetails>(message, cancellationToken);
    }

    /// <summary>Uploads a CSV as multipart form data (source, file).</summary>
    public virtual async Task<ApiResult<ImportBatchDetails>> ImportAsync(string source, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(source), "source");
        var file = new StreamContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        return await SendAsync<ImportBatchDetails>(new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/imports") { Content = form }, cancellationToken)
            .ConfigureAwait(false);
    }

    public virtual Task<ApiResult<PagedResponse<ImportBatchResponse>>> ListImportsAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResponse<ImportBatchResponse>>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/inventory/imports?page={page}&pageSize={pageSize}")),
            cancellationToken);
}
