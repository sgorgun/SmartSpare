using System.Globalization;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Fleet;

/// <summary>The Fleet API, <c>/api/v1/fleet</c> (STAGE_6_PLAN section 6: one typed client per module).</summary>
public class FleetClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<PagedResponse<MachineSummary>>> ListMachinesAsync(
        string? search, short? warehouseId, string? dueStatus, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        if (warehouseId is { } warehouse)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&warehouseId={warehouse}");
        }

        if (!string.IsNullOrEmpty(dueStatus))
        {
            query += "&dueStatus=" + Uri.EscapeDataString(dueStatus);
        }

        return SendAsync<PagedResponse<MachineSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/fleet/machines?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<MachineDetails>> GetMachineAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync<MachineDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/fleet/machines/{id}")), cancellationToken);

    /// <summary>Records a reading; an identical retry (same time and hours) is answered with the stored one.</summary>
    public virtual Task<ApiResult<RecordReadingResponse>> RecordReadingAsync(long machineId, RecordReadingRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RecordReadingResponse>(
            new HttpRequestMessage(HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"api/v1/fleet/machines/{machineId}/readings"))
            {
                Content = JsonContent.Create(request),
            },
            cancellationToken);

    public virtual Task<ApiResult<PagedResponse<ServiceOrderSummary>>> ListServiceOrdersAsync(
        string? status, short? warehouseId, long? machineId, int page, int pageSize, CancellationToken cancellationToken = default)
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

        if (machineId is { } machine)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&machineId={machine}");
        }

        return SendAsync<PagedResponse<ServiceOrderSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/fleet/service-orders?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<PagedResponse<DueMaintenanceRow>>> GetDueMaintenanceAsync(
        short? warehouseId, int horizonDays, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"horizonDays={horizonDays}&page={page}&pageSize={pageSize}");
        if (warehouseId is { } warehouse)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&warehouseId={warehouse}");
        }

        if (!string.IsNullOrEmpty(status))
        {
            query += "&status=" + Uri.EscapeDataString(status);
        }

        return SendAsync<PagedResponse<DueMaintenanceRow>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/fleet/maintenance/due?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<ServiceOrderDetails>> GetServiceOrderAsync(long id, CancellationToken cancellationToken = default) =>
        SendAsync<ServiceOrderDetails>(
            new HttpRequestMessage(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"api/v1/fleet/service-orders/{id}")), cancellationToken);

    public virtual Task<ApiResult<ServiceOrderDetails>> CreateServiceOrderAsync(CreateServiceOrderRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ServiceOrderDetails>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/fleet/service-orders") { Content = JsonContent.Create(request) }, cancellationToken);

    /// <summary>
    /// One of <see cref="ServiceOrderActions"/>: start (no body), complete (<paramref name="engineHours"/>) or cancel
    /// (<paramref name="reason"/>).
    /// </summary>
    public virtual Task<ApiResult<ServiceOrderDetails>> ServiceOrderActionAsync(
        long id, string action, decimal? engineHours = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"api/v1/fleet/service-orders/{id}/{Uri.EscapeDataString(action)}"))
        {
            Content = action switch
            {
                ServiceOrderActions.Complete => JsonContent.Create(new CompleteServiceOrderRequest(engineHours ?? 0)),
                ServiceOrderActions.Cancel => JsonContent.Create(new CancelServiceOrderRequest(reason ?? string.Empty)),
                _ => null,
            },
        };
        return SendAsync<ServiceOrderDetails>(request, cancellationToken);
    }

    /// <summary>Sets the kit of an order that has not started (PUT /service-orders/{id}/lines).</summary>
    public virtual Task<ApiResult<ServiceOrderDetails>> ChangeServiceOrderLinesAsync(
        long id, IReadOnlyList<CreateServiceOrderLine> lines, CancellationToken cancellationToken = default) =>
        SendAsync<ServiceOrderDetails>(
            new HttpRequestMessage(HttpMethod.Put, string.Create(CultureInfo.InvariantCulture, $"api/v1/fleet/service-orders/{id}/lines"))
            {
                Content = JsonContent.Create(new ChangeServiceOrderLinesRequest(lines)),
            },
            cancellationToken);
}
