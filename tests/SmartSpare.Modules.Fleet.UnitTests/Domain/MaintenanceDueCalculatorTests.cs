using SmartSpare.Modules.Fleet.Domain.Maintenance;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The maintenance due calculation of STAGE_6_PLAN section 2 with hand-computed cases. The worked example of
/// docs/architecture/MAINTENANCE_PLANNING.md is <see cref="The_worked_example_of_the_documentation"/>.
/// </summary>
public sealed class MaintenanceDueCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    private static ReadingPoint At(DateOnly day, decimal hours) => new(new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero), hours);

    private static MaintenanceDueInput Input(
        decimal currentHours,
        ReadingPoint[]? readings = null,
        int intervalHours = 500,
        short? intervalMonths = null,
        decimal factor = 1.0m,
        decimal? lastServed = null,
        decimal? openOrder = null,
        DateOnly? lastCompleted = null,
        DateOnly? commissioned = null)
    {
        readings ??= [At(Today.AddDays(-1), currentHours)];
        return new MaintenanceDueInput(intervalHours, intervalMonths, factor, currentHours,
            readings.Length > 0 ? readings[0].Hours : null, lastServed, openOrder, lastCompleted, commissioned, readings);
    }

    // --- 2.1 engine-hour threshold -------------------------------------------------------------------------------

    [Fact]
    public void UC02_512_hours_cross_the_500_hour_service()
    {
        // First known reading 440 h: L = ⌊440 / 500⌋ · 500 = 0, D = max(500, ⌊512 / 500⌋ · 500) = 500, R = −12.
        var due = MaintenanceDueCalculator.Calculate(
            Input(512, [At(Today.AddDays(-20), 440), At(Today, 512)]), Today);

        due.EffectiveInterval.ShouldBe(500.0m);
        due.DueEngineHours.ShouldBe(500m);
        due.RemainingHours.ShouldBe(-12m);
        due.Status.ShouldBe(MaintenanceDueStatus.Overdue);
        due.ProjectedDueDate.ShouldBe(Today);
    }

    [Fact]
    public void An_open_order_makes_the_plan_planned_and_the_next_threshold_follows_it()
    {
        // The drafted 500 h order counts as served (L = 500), as in fn_maintenance_due: D = 1000.
        var due = MaintenanceDueCalculator.Calculate(
            Input(512, [At(Today.AddDays(-20), 440), At(Today, 512)], lastServed: 500, openOrder: 500), Today);

        due.Status.ShouldBe(MaintenanceDueStatus.Planned);
        due.DueEngineHours.ShouldBe(1000m);
        due.RemainingHours.ShouldBe(488m);
    }

    [Fact]
    public void After_the_500_hour_service_the_next_threshold_is_1000()
    {
        var due = MaintenanceDueCalculator.Calculate(Input(512, lastServed: 500), Today);

        due.DueEngineHours.ShouldBe(1000m);
        due.RemainingHours.ShouldBe(488m);
    }

    [Fact]
    public void Of_several_skipped_thresholds_only_the_latest_crossed_one_is_due()
    {
        // L = 500, h = 1720: max((1 + 1) · 500, ⌊1720 / 500⌋ · 500) = max(1000, 1500) = 1500.
        MaintenanceDueCalculator.NextThreshold(500m, 1720m, 300m, 500m).ShouldBe(1500m);
    }

    [Fact]
    public void Thresholds_before_the_first_reading_are_unknown_history()
    {
        // A machine imported at 1230 h: L = ⌊1230 / 500⌋ · 500 = 1000, so the next service is 1500, not 500.
        MaintenanceDueCalculator.NextThreshold(500m, 1230m, 1230m, null).ShouldBe(1500m);
    }

    [Fact]
    public void Without_readings_the_history_starts_at_zero()
    {
        MaintenanceDueCalculator.NextThreshold(500m, 0m, null, null).ShouldBe(500m);
    }

    [Fact]
    public void A_severe_condition_shortens_the_interval()
    {
        // F5: factor 0.8 → E = 400; at 390 h ten hours remain → due soon (≤ 50 h).
        var due = MaintenanceDueCalculator.Calculate(Input(390, [At(Today.AddDays(-30), 0), At(Today, 390)], factor: 0.8m), Today);

        due.EffectiveInterval.ShouldBe(400.0m);
        due.DueEngineHours.ShouldBe(400m);
        due.RemainingHours.ShouldBe(10m);
        due.Status.ShouldBe(MaintenanceDueStatus.DueSoon);
    }

    [Theory]
    [InlineData(500, 1.0, 500.0)]
    [InlineData(600, 0.85, 510.0)]
    [InlineData(333, 0.85, 283.1)]
    [InlineData(750, 0.7, 525.0)]
    public void The_effective_interval_rounds_to_one_decimal_like_SQL(int interval, double factor, double expected)
    {
        MaintenanceDueCalculator.EffectiveInterval(interval, (decimal)factor).ShouldBe((decimal)expected);
    }

    // --- 2.2 usage rate -------------------------------------------------------------------------------------------

    [Fact]
    public void The_usage_rate_is_the_increase_over_the_days_between_the_readings()
    {
        var rate = UsageRate.From([At(Today.AddDays(-70), 300), At(Today, 440)]);

        rate.HoursPerDay.ShouldBe(2m);
        rate.SpanDays.ShouldBe(70);
    }

    [Fact]
    public void Readings_older_than_the_window_are_ignored()
    {
        // Window: 365 days before the latest reading (day 700) starts at day 335 → first reading used is day 400.
        var start = Today.AddDays(-700);
        var rate = UsageRate.From([At(start, 0), At(start.AddDays(400), 800), At(start.AddDays(700), 900)]);

        rate.HoursPerDay!.Value.ShouldBe(100m / 300m, tolerance: 0.0001m);
    }

    [Fact]
    public void A_window_shorter_than_14_days_falls_back_to_the_whole_history()
    {
        var start = Today.AddDays(-605);
        var rate = UsageRate.From([At(start, 0), At(start.AddDays(600), 600), At(start.AddDays(605), 610)]);

        rate.HoursPerDay!.Value.ShouldBe(610m / 605m, tolerance: 0.0001m);
    }

    [Fact]
    public void The_rate_is_unknown_with_one_reading_or_a_history_shorter_than_14_days()
    {
        UsageRate.From([At(Today, 100)]).IsKnown.ShouldBeFalse();
        UsageRate.From([]).ShouldBe(UsageRate.Unknown);
        UsageRate.From([At(Today.AddDays(-13), 100), At(Today, 150)]).IsKnown.ShouldBeFalse();
    }

    // --- 2.3 projected date ---------------------------------------------------------------------------------------

    [Fact]
    public void The_worked_example_of_the_documentation()
    {
        // 300 h on 28 Aug, 440 h on 6 Nov: u = 140 / 70 = 2 h/day. D = 500, R = 60, so t_h = 6 Nov + 30 days = 6 Dec.
        // Calendar: commissioned 15 Jun 2025, 12 months → 15 Jun 2026, already passed → overdue by calendar.
        var readings = new[] { At(new DateOnly(2026, 8, 28), 300), At(new DateOnly(2026, 11, 6), 440) };

        var hoursOnly = MaintenanceDueCalculator.Calculate(Input(440, readings), Today);
        hoursOnly.Usage.HoursPerDay.ShouldBe(2m);
        hoursOnly.RemainingHours.ShouldBe(60m);
        hoursOnly.ProjectedDueDate.ShouldBe(new DateOnly(2026, 12, 6));
        hoursOnly.DueDate.ShouldBe(new DateOnly(2026, 12, 6));
        hoursOnly.Trigger.ShouldBe(MaintenanceDueTrigger.EngineHours);
        hoursOnly.Status.ShouldBe(MaintenanceDueStatus.DueSoon);   // within 14 days

        var withCalendar = MaintenanceDueCalculator.Calculate(
            Input(440, readings, intervalMonths: 12, commissioned: new DateOnly(2025, 6, 15)), Today);
        withCalendar.CalendarDueDate.ShouldBe(new DateOnly(2026, 6, 15));
        withCalendar.DueDate.ShouldBe(new DateOnly(2026, 6, 15));
        withCalendar.Trigger.ShouldBe(MaintenanceDueTrigger.Calendar);
        withCalendar.Status.ShouldBe(MaintenanceDueStatus.Overdue);
    }

    [Fact]
    public void A_due_date_beyond_the_planning_horizon_is_ok_and_a_longer_horizon_makes_it_due_soon()
    {
        var readings = new[] { At(new DateOnly(2026, 8, 28), 300), At(new DateOnly(2026, 11, 6), 440) };

        MaintenanceDueCalculator.Calculate(Input(440, readings), new DateOnly(2026, 11, 6)).Status.ShouldBe(MaintenanceDueStatus.Ok);
        MaintenanceDueCalculator.Calculate(Input(440, readings), new DateOnly(2026, 11, 6), planningHorizonDays: 30)
            .Status.ShouldBe(MaintenanceDueStatus.DueSoon);
    }

    [Fact]
    public void Without_a_usage_rate_there_is_no_projected_date_and_the_status_follows_the_hours()
    {
        var far = MaintenanceDueCalculator.Calculate(Input(440, [At(Today.AddDays(-3), 440)]), Today);
        far.Usage.IsKnown.ShouldBeFalse();
        far.ProjectedDueDate.ShouldBeNull();
        far.DueDate.ShouldBeNull();
        far.Status.ShouldBe(MaintenanceDueStatus.Ok);

        MaintenanceDueCalculator.Calculate(Input(460, [At(Today.AddDays(-3), 460)]), Today).Status.ShouldBe(MaintenanceDueStatus.DueSoon);
    }

    [Fact]
    public void An_idle_machine_has_no_projected_date()
    {
        var due = MaintenanceDueCalculator.Calculate(Input(440, [At(Today.AddDays(-100), 440), At(Today, 440)]), Today);

        due.Usage.HoursPerDay.ShouldBe(0m);
        due.ProjectedDueDate.ShouldBeNull();
    }

    [Fact]
    public void A_projection_beyond_a_hundred_years_is_dropped()
    {
        MaintenanceDueCalculator.ProjectedDate(400m, UsageRate.From([At(Today.AddDays(-36_500), 0), At(Today, 0.1m)]), Today)
            .ShouldBeNull();
        MaintenanceDueCalculator.ProjectedDate(10m, UsageRate.Unknown, null).ShouldBeNull();
    }

    // --- 2.4 calendar ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_calendar_anchor_is_the_last_completion_then_commissioning_then_the_first_reading()
    {
        var readings = new[] { At(new DateOnly(2024, 3, 10), 100), At(Today, 900) };

        MaintenanceDueCalculator.CalendarDate(12, new DateOnly(2026, 3, 1), new DateOnly(2025, 6, 15), readings).ShouldBe(new DateOnly(2027, 3, 1));
        MaintenanceDueCalculator.CalendarDate(12, null, new DateOnly(2025, 6, 15), readings).ShouldBe(new DateOnly(2026, 6, 15));
        MaintenanceDueCalculator.CalendarDate(24, null, null, readings).ShouldBe(new DateOnly(2026, 3, 10));
        MaintenanceDueCalculator.CalendarDate(12, null, null, []).ShouldBeNull();
        MaintenanceDueCalculator.CalendarDate(null, new DateOnly(2026, 3, 1), null, readings).ShouldBeNull();
    }

    [Fact]
    public void Whichever_comes_first_the_engine_hours_can_win_over_the_calendar()
    {
        // Busy machine: 10 h/day, 100 h to go → 10 days; calendar due in five months.
        var readings = new[] { At(Today.AddDays(-30), 100), At(Today, 400) };

        var due = MaintenanceDueCalculator.Calculate(Input(400, readings, intervalMonths: 12, lastCompleted: Today.AddMonths(-7)), Today);

        due.ProjectedDueDate.ShouldBe(Today.AddDays(10));
        due.CalendarDueDate.ShouldBe(Today.AddMonths(5));
        due.Trigger.ShouldBe(MaintenanceDueTrigger.EngineHours);
        due.Status.ShouldBe(MaintenanceDueStatus.DueSoon);
    }

    [Fact]
    public void A_calendar_date_without_a_usage_rate_is_the_due_date()
    {
        var due = MaintenanceDueCalculator.Calculate(
            Input(100, [At(Today.AddDays(-2), 100)], intervalMonths: 12, lastCompleted: Today.AddMonths(-12).AddDays(10)), Today);

        due.DueDate.ShouldBe(Today.AddDays(10));
        due.Trigger.ShouldBe(MaintenanceDueTrigger.Calendar);
        due.Status.ShouldBe(MaintenanceDueStatus.DueSoon);
    }

    [Fact]
    public void Invalid_arguments_are_programming_errors()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => MaintenanceDueCalculator.EffectiveInterval(0, 1m));
        Should.Throw<ArgumentOutOfRangeException>(() => MaintenanceDueCalculator.EffectiveInterval(500, 0m));
        Should.Throw<ArgumentOutOfRangeException>(() => MaintenanceDueCalculator.Calculate(Input(10), Today, -1));
    }
}
