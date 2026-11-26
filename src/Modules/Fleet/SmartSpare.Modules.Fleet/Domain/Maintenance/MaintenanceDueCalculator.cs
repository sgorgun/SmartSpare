namespace SmartSpare.Modules.Fleet.Domain.Maintenance;

/// <summary>Due status of a maintenance plan on a machine (STAGE_6_PLAN section 2.5).</summary>
internal enum MaintenanceDueStatus
{
    /// <summary>Not due within the horizons.</summary>
    Ok,

    /// <summary>An open service order of the plan exists; it covers the latest due threshold, so D is the one after it.</summary>
    Planned,

    /// <summary>Within 50 engine hours or within the planning horizon in days.</summary>
    DueSoon,

    /// <summary>The engine-hour threshold or the calendar date has passed.</summary>
    Overdue,
}

/// <summary>Which interval makes the plan due first.</summary>
internal enum MaintenanceDueTrigger
{
    EngineHours,
    Calendar,
}

/// <summary>One odometer reading as input of the calculation.</summary>
internal readonly record struct ReadingPoint(DateTimeOffset ReadAt, decimal Hours);

/// <summary>
/// Everything the calculation needs about one plan on one machine, read set-based by the infrastructure
/// (<c>MaintenanceDueInputs</c>); no I/O happens during the calculation.
/// </summary>
/// <param name="IntervalHours">OEM interval in engine hours (H).</param>
/// <param name="IntervalMonths">OEM calendar interval in months (M), if any.</param>
/// <param name="ConditionFactor">Interval factor of the machine's operating condition (f).</param>
/// <param name="CurrentHours">Current engine hours (h), the latest reading.</param>
/// <param name="FirstReadingHours">The first known reading (h₀); null without readings.</param>
/// <param name="LastServedThreshold">Highest due threshold of the plan's non-cancelled orders; null if none.</param>
/// <param name="OpenOrderThreshold">Due threshold of an open order of the plan; null if none.</param>
/// <param name="LastCompletedOn">Completion date of the plan's last completed order; null if none.</param>
/// <param name="CommissionedOn">Commissioning date of the machine; null if unknown.</param>
/// <param name="Readings">Readings in time order (at least the look-back window before the latest one).</param>
internal sealed record MaintenanceDueInput(
    int IntervalHours,
    short? IntervalMonths,
    decimal ConditionFactor,
    decimal CurrentHours,
    decimal? FirstReadingHours,
    decimal? LastServedThreshold,
    decimal? OpenOrderThreshold,
    DateOnly? LastCompletedOn,
    DateOnly? CommissionedOn,
    IReadOnlyList<ReadingPoint> Readings);

/// <summary>The outcome for one plan on one machine.</summary>
/// <param name="EffectiveInterval">E = round(H · f, 1).</param>
/// <param name="DueEngineHours">Next due threshold D.</param>
/// <param name="RemainingHours">R = D − h; zero or negative when overdue.</param>
/// <param name="Usage">Average engine hours per day (u).</param>
/// <param name="ProjectedDueDate">t_h, the day the machine reaches D at rate u; null if u is unknown or zero.</param>
/// <param name="CalendarDueDate">t_c = anchor + M months; null without a calendar interval or anchor.</param>
/// <param name="DueDate">min(t_h, t_c): the day the plan is due, whichever interval comes first.</param>
/// <param name="Trigger">The interval that gives <paramref name="DueDate"/> (engine hours when no date is known).</param>
/// <param name="Status">Section 2.5.</param>
internal sealed record MaintenanceDue(
    decimal EffectiveInterval,
    decimal DueEngineHours,
    decimal RemainingHours,
    UsageRate Usage,
    DateOnly? ProjectedDueDate,
    DateOnly? CalendarDueDate,
    DateOnly? DueDate,
    MaintenanceDueTrigger Trigger,
    MaintenanceDueStatus Status);

/// <summary>
/// The maintenance due calculation (STAGE_6_PLAN section 2, docs/architecture/MAINTENANCE_PLANNING.md). Pure: the
/// caller passes the inputs and today's date. The engine-hour threshold equals <c>fleet.fn_maintenance_due</c>, which
/// keeps deciding the automatic drafts on a reading (US05); this service adds the usage-based projection and the
/// calendar interval for planning.
/// </summary>
internal static class MaintenanceDueCalculator
{
    /// <summary>US05: a planned service is drafted when at most this many engine hours remain.</summary>
    public const decimal DraftingHorizonHours = 50m;

    /// <summary>Default planning horizon of the due list in days.</summary>
    public const int DefaultPlanningHorizonDays = 14;

    public static MaintenanceDue Calculate(MaintenanceDueInput input, DateOnly today, int planningHorizonDays = DefaultPlanningHorizonDays)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(planningHorizonDays);

        var e = EffectiveInterval(input.IntervalHours, input.ConditionFactor);
        var d = NextThreshold(e, input.CurrentHours, input.FirstReadingHours, input.LastServedThreshold);
        var r = d - input.CurrentHours;

        var usage = UsageRate.From(input.Readings);
        var lastReadAt = input.Readings.Count > 0 ? DateOnly.FromDateTime(input.Readings[^1].ReadAt.UtcDateTime) : (DateOnly?)null;
        var projected = ProjectedDate(r, usage, lastReadAt);
        var calendar = CalendarDate(input.IntervalMonths, input.LastCompletedOn, input.CommissionedOn, input.Readings);

        var (dueDate, trigger) = (projected, calendar) switch
        {
            ({ } h, { } c) => c < h ? (c, MaintenanceDueTrigger.Calendar) : (h, MaintenanceDueTrigger.EngineHours),
            (null, { } c) => ((DateOnly?)c, MaintenanceDueTrigger.Calendar),
            ({ } h, null) => ((DateOnly?)h, MaintenanceDueTrigger.EngineHours),
            _ => ((DateOnly?)null, MaintenanceDueTrigger.EngineHours),
        };

        var status = Status(r, calendar, dueDate, input.OpenOrderThreshold, today, planningHorizonDays);
        return new MaintenanceDue(e, d, r, usage, projected, calendar, dueDate, trigger, status);
    }

    /// <summary>E = round(H · f, 1), as <c>round(mp.interval_hours * m.interval_factor, 1)</c> in SQL.</summary>
    public static decimal EffectiveInterval(int intervalHours, decimal conditionFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalHours);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(conditionFactor);
        return decimal.Round(intervalHours * conditionFactor, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// D = max((⌊L / E⌋ + 1) · E, ⌊h / E⌋ · E), with L the last served threshold or, without one, ⌊h₀ / E⌋ · E:
    /// thresholds before the first known reading are unknown history, and of several skipped thresholds only the
    /// latest crossed one is due.
    /// </summary>
    public static decimal NextThreshold(decimal effectiveInterval, decimal currentHours, decimal? firstReadingHours, decimal? lastServedThreshold)
    {
        var e = effectiveInterval;
        var l = lastServedThreshold ?? Math.Floor((firstReadingHours ?? 0) / e) * e;
        return Math.Max((Math.Floor(l / e) + 1) * e, Math.Floor(currentHours / e) * e);
    }

    /// <summary>t_h = t_last + R / u days (rounded up to whole days); the last reading's day when already due.</summary>
    public static DateOnly? ProjectedDate(decimal remainingHours, UsageRate usage, DateOnly? lastReadOn)
    {
        if (lastReadOn is not { } last)
        {
            return null;
        }

        if (remainingHours <= 0)
        {
            return last;
        }

        if (usage.HoursPerDay is not { } u || u <= 0)
        {
            return null;
        }

        var days = Math.Ceiling(remainingHours / u);
        return days > 36_500 ? null : last.AddDays((int)days);
    }

    /// <summary>
    /// t_c = A + M months, with the anchor A = last completion of the plan, else commissioning, else first reading.
    /// </summary>
    public static DateOnly? CalendarDate(short? intervalMonths, DateOnly? lastCompletedOn, DateOnly? commissionedOn, IReadOnlyList<ReadingPoint> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (intervalMonths is not { } m)
        {
            return null;
        }

        var anchor = lastCompletedOn ?? commissionedOn ?? (readings.Count > 0 ? DateOnly.FromDateTime(readings[0].ReadAt.UtcDateTime) : null);
        return anchor?.AddMonths(m);
    }

    private static MaintenanceDueStatus Status(decimal remaining, DateOnly? calendar, DateOnly? dueDate, decimal? openOrderThreshold,
        DateOnly today, int horizonDays)
    {
        // An open order counts as served in L (as in fn_maintenance_due), so D is already the threshold after it.
        if (openOrderThreshold is not null)
        {
            return MaintenanceDueStatus.Planned;
        }

        if (remaining <= 0 || calendar <= today)
        {
            return MaintenanceDueStatus.Overdue;
        }

        return remaining <= DraftingHorizonHours || dueDate <= today.AddDays(horizonDays)
            ? MaintenanceDueStatus.DueSoon
            : MaintenanceDueStatus.Ok;
    }
}
