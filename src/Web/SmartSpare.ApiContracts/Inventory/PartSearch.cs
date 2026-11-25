namespace SmartSpare.ApiContracts.Inventory;

/// <summary>Limits of <c>GET /api/v1/inventory/parts</c>, shared by the endpoint's validator and the parts page.</summary>
public static class PartSearch
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    /// <summary>Longest search text: the longest description a part can have.</summary>
    public const int MaxSearchLength = 300;
}
