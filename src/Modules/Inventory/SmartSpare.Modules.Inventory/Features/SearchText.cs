namespace SmartSpare.Modules.Inventory.Features;

/// <summary>
/// The search semantics of the Inventory lists: a case-insensitive "contains" on the searched columns, where the text
/// matches literally (<c>%</c>, <c>_</c> and <c>\</c> lose their LIKE meaning). Empty text searches nothing.
/// </summary>
internal static class SearchText
{
    /// <summary>The ILIKE pattern of <paramref name="search"/>, or <see langword="null"/> for no filter.</summary>
    public static string? Pattern(string? search)
    {
        var text = search?.Trim();
        return string.IsNullOrEmpty(text) ? null : "%" + Escape(text) + "%";
    }

    /// <summary>Escapes the LIKE wildcards and the escape character.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
