using SmartSpare.Web.Client.Features.Fleet;

namespace SmartSpare.Web.Tests;

/// <summary>The client-side rules of the engine-hours entry (STAGE_6_PLAN section 6): what a tablet keyboard types, in order.</summary>
public sealed class EngineHoursInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LastAt = Now.AddDays(-2);

    [Theory]
    [InlineData("1505", 1505)]
    [InlineData("1505.5", 1505.5)]
    [InlineData("1505,5", 1505.5)]
    [InlineData(" 1 505,5 ", 1505.5)]
    [InlineData("1 505", 1505)]
    public void Typed_hours_are_read_with_a_comma_or_a_point(string text, decimal hours) =>
        EngineHoursInput.Parse(text).ShouldBe(hours);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.505,5")]
    public void Anything_else_is_not_a_number(string? text) => EngineHoursInput.Parse(text).ShouldBeNull();

    [Theory]
    [InlineData("", EngineHoursProblem.Missing)]
    [InlineData("twelve", EngineHoursProblem.NotANumber)]
    [InlineData("100000.1", EngineHoursProblem.OutOfRange)]
    [InlineData("1505.55", EngineHoursProblem.TooManyDecimals)]
    [InlineData("1491.9", EngineHoursProblem.BelowLastReading)]
    [InlineData("1492", EngineHoursProblem.None)]
    [InlineData("1505.5", EngineHoursProblem.None)]
    public void The_hours_are_checked_against_the_rules_and_the_last_reading(string text, EngineHoursProblem problem) =>
        EngineHoursInput.Check(text, Now, 1492, LastAt, Now).ShouldBe(problem);

    [Fact]
    public void The_time_is_after_the_last_reading_and_not_in_the_future_beyond_the_tolerance()
    {
        EngineHoursInput.Check("1500", LastAt, 1492, LastAt, Now).ShouldBe(EngineHoursProblem.BeforeLastReading);
        EngineHoursInput.Check("1500", Now.AddMinutes(6), 1492, LastAt, Now).ShouldBe(EngineHoursProblem.InTheFuture);
        EngineHoursInput.Check("1500", Now.AddMinutes(4), 1492, LastAt, Now).ShouldBe(EngineHoursProblem.None);
    }

    [Fact]
    public void A_machine_without_readings_takes_any_valid_value() =>
        EngineHoursInput.Check("0", Now, null, null, Now).ShouldBe(EngineHoursProblem.None);
}
