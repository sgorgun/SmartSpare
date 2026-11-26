using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Features.Stock;

/// <summary>The API names of the stock statuses; the overview's SQL produces the same strings (StockApiTests).</summary>
internal static class StockStatusNames
{
    /// <summary>The API name of a domain status (the same strings the SQL above produces).</summary>
    public static string ToApi(StockStatus status) => status switch
    {
        StockStatus.Out => StockStatuses.Out,
        StockStatus.BelowSafety => StockStatuses.BelowSafety,
        StockStatus.AboveMax => StockStatuses.AboveMax,
        _ => StockStatuses.Ok,
    };
}
