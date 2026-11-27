using System.Globalization;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// The <c>xmin</c> concurrency version of an aggregate as an opaque string and an entity tag: reads return
/// <c>ETag: "123"</c>, writes send it back as <c>If-Match</c> (optimistic concurrency, DATABASE_DESIGN 6.2).
/// </summary>
public static class Versions
{
    public static string Format(uint version) => version.ToString(CultureInfo.InvariantCulture);

    public static string ETag(uint version) => $"\"{Format(version)}\"";

    /// <summary>Reads <c>If-Match: "123"</c> (or a bare <c>123</c>); <see langword="null"/> if absent or malformed.</summary>
    public static uint? Parse(string? ifMatch)
    {
        var value = ifMatch?.Trim();
        if (value is { Length: > 2 } && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;
    }
}
