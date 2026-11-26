using System.Net.Http.Json;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Contracts;

/// <summary>
/// <see cref="IWarehouseQueries"/> over HTTP for another service (ADR 0015 §4, STAGE_5_PLAN section 4.3). The host
/// registers it as a typed client whose handlers forward the caller's internal token and add resilience and service
/// discovery. Transport failures and unexpected answers throw <see cref="DependencyUnavailableException"/> (503), never
/// a wrong answer.
/// </summary>
public sealed class HttpWarehouseQueries(HttpClient client) : IWarehouseQueries
{
    public async Task<IReadOnlyList<WarehouseInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFromJsonAsync<List<WarehouseInfo>>(
                    new Uri(InventoryInternalApi.Warehouses, UriKind.Relative), cancellationToken).ConfigureAwait(false)
                ?? throw Unavailable("The Inventory service answered the warehouse query without a body.");
        }
        // Transport failures, error statuses, timeouts (not the caller's cancellation) and the resilience handler's
        // rejections. Polly's exceptions are matched by namespace, so the contract needs no Polly reference.
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                                   || ex.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true)
        {
            throw Unavailable("The Inventory service did not answer the warehouse query.", ex);
        }
    }

    public async Task<bool> ExistsAsync(short warehouseId, CancellationToken cancellationToken) =>
        (await GetAllAsync(cancellationToken).ConfigureAwait(false)).Any(w => w.Id == warehouseId && w.IsActive);

    private static DependencyUnavailableException Unavailable(string message, Exception? inner = null) =>
        inner is null ? new(message) { Service = "inventory" } : new(message, inner) { Service = "inventory" };
}
