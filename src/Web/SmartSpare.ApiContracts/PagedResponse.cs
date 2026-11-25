using System.ComponentModel;

namespace SmartSpare.ApiContracts;

/// <summary>One page of a list: the items of page <see cref="Page"/> (1-based) and the total number of matches.</summary>
[Description("One page of a list.")]
public sealed record PagedResponse<T>(
    [property: Description("The items of this page.")] IReadOnlyList<T> Items,
    [property: Description("Page number, from 1.")] int Page,
    [property: Description("Requested page size.")] int PageSize,
    [property: Description("Number of all matches across every page.")] int TotalCount);
