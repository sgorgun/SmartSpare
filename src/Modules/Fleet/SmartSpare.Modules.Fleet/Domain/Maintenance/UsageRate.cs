namespace SmartSpare.Modules.Fleet.Domain.Maintenance;

/// <summary>
/// Average engine hours per calendar day of a machine (STAGE_6_PLAN section 2.2):
/// u = (h_last − h_first) / days(t_first, t_last) over the readings of the look-back window before the latest reading.
/// Known only when the window holds two readings at least <see cref="MinSpanDays"/> days apart; otherwise the whole
/// history is used under the same rule; otherwise the rate is unknown. The window spans whole seasons on purpose:
/// machines run in campaigns, and a short window would project a harvest-week rate over the winter.
/// </summary>
internal readonly record struct UsageRate
{
    public const int WindowDays = 365;
    public const int MinSpanDays = 14;

    private UsageRate(decimal? hoursPerDay, int spanDays)
    {
        HoursPerDay = hoursPerDay;
        SpanDays = spanDays;
    }

    public static UsageRate Unknown => default;

    /// <summary>Average engine hours per day; null when unknown.</summary>
    public decimal? HoursPerDay { get; }

    /// <summary>Days between the first and the last reading used.</summary>
    public int SpanDays { get; }

    public bool IsKnown => HoursPerDay is not null;

    /// <summary>The rate from readings in time order.</summary>
    public static UsageRate From(IReadOnlyList<ReadingPoint> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count < 2)
        {
            return Unknown;
        }

        var last = readings[^1];
        var windowStart = last.ReadAt.AddDays(-WindowDays);
        var first = readings.FirstOrDefault(r => r.ReadAt >= windowStart);
        var rate = Between(first, last);
        return rate.IsKnown ? rate : Between(readings[0], last);
    }

    private static UsageRate Between(ReadingPoint first, ReadingPoint last)
    {
        var span = last.ReadAt - first.ReadAt;
        if (span.TotalDays < MinSpanDays)
        {
            return Unknown;
        }

        var perDay = (last.Hours - first.Hours) / (decimal)span.TotalDays;
        return new UsageRate(perDay, (int)Math.Floor(span.TotalDays));
    }
}
