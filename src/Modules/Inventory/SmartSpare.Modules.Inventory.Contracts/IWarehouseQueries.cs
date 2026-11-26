namespace SmartSpare.Modules.Inventory.Contracts;

/// <summary>
/// Read-only queries on the warehouse network for other modules (MODULE_MAPPING 2.2, 3.5): the seven hubs are static
/// reference data of Inventory. In the single host the module answers in process; between services the
/// <see cref="HttpWarehouseQueries"/> client calls <see cref="InventoryInternalApi.Warehouses"/> with the caller's
/// internal token.
/// </summary>
public interface IWarehouseQueries
{
    /// <summary>All warehouses, active or not, ordered by code.</summary>
    Task<IReadOnlyList<WarehouseInfo>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Whether an active warehouse with this id exists.</summary>
    Task<bool> ExistsAsync(short warehouseId, CancellationToken cancellationToken);
}

/// <summary>A warehouse as other modules see it.</summary>
public sealed record WarehouseInfo(short Id, string Code, string Name, bool IsCentralHub, bool IsActive);

/// <summary>The Inventory service's internal endpoints (ADR 0015 §4); never routed by the gateway.</summary>
public static class InventoryInternalApi
{
    public const string Prefix = "/internal/v1/inventory";

    public const string Warehouses = Prefix + "/warehouses";
}
