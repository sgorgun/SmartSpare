using System.Globalization;

namespace SmartSpare.Web.Client.Features.Fleet;

/// <summary>Why an engine-hours entry cannot be sent; each has a plain-language text in the resources.</summary>
public enum EngineHoursProblem
{
    None,
    Missing,
    NotANumber,
    OutOfRange,
    TooManyDecimals,
    BelowLastReading,
    BeforeLastReading,
    InTheFuture,
}

/// <summary>
/// The client-side rules of the engine-hours entry (STAGE_6_PLAN section 6), checked before anything is sent: the hours
/// as typed on a phone or tablet keyboard (decimal comma or point, spaces as thousands separators), not below the last
/// reading, the time not before it and not in the future. The server checks the same rules again (SS100–SS104).
/// </summary>
public static class EngineHoursInput
{
    /// <summary>Same tolerance as the server (SS104): five minutes for clocks that are slightly ahead.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Reads "1 505,5", "1505.5" or "1505" as hours; null when the text is not a number.</summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace(' '.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) ? hours : null;
    }

    /// <summary>The first rule the entry breaks, or <see cref="EngineHoursProblem.None"/>.</summary>
    public static EngineHoursProblem Check(string? text, DateTimeOffset readAt, decimal? lastHours, DateTimeOffset? lastReadAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return EngineHoursProblem.Missing;
        }

        if (Parse(text) is not { } hours)
        {
            return EngineHoursProblem.NotANumber;
        }

        if (hours is < 0 or > ApiContracts.Fleet.RecordReadingRequestValidator.MaxHours)
        {
            return EngineHoursProblem.OutOfRange;
        }

        if (decimal.Round(hours, 1) != hours)
        {
            return EngineHoursProblem.TooManyDecimals;
        }

        if (readAt > now + ClockTolerance)
        {
            return EngineHoursProblem.InTheFuture;
        }

        if (lastReadAt is { } previous && readAt <= previous)
        {
            return EngineHoursProblem.BeforeLastReading;
        }

        return lastHours is { } last && hours < last ? EngineHoursProblem.BelowLastReading : EngineHoursProblem.None;
    }
}
