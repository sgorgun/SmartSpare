using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Machines;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The odometer rules of a reading (UC02 step 3 and A1). They mirror <c>fleet.trg_engine_hours_validate</c>
/// (SS100–SS103) and the CHECKs of <c>fleet.engine_hours_reading</c>.
/// </summary>
public sealed class MachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private static Machine Lexion(MachineStatus status = MachineStatus.Active) =>
        new(1, "LX901PNV2026", 7, 3, 1, 2, 2025, status, new DateOnly(2025, 6, 15));

    private static EngineHoursReading Last(decimal hours, DateTimeOffset readAt) => new(1, hours, readAt, ReadingSource.ServiceVisit);

    [Fact]
    public void The_first_reading_of_a_machine_is_accepted()
    {
        Lexion().CheckReading(440, Now.AddDays(-20), null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_later_and_higher_plausible_reading_is_accepted()
    {
        // UC02: 440 h twenty days ago, 512 h now (+72 h within 480 h of wall-clock time).
        Lexion().CheckReading(512, Now, Last(440, Now.AddDays(-20)), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unchanged_odometer_is_accepted()
    {
        Lexion().CheckReading(440, Now, Last(440, Now.AddDays(-1)), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_rollback_is_refused_with_SS100()
    {
        // UC02 A1: 312 h after 440 h.
        var result = Lexion().CheckReading(312, Now, Last(440, Now.AddDays(-5)), Now);

        result.Error.ShouldBe(FleetErrors.ReadingDecreased);
        result.Error.Code.ShouldBe("SS100");
    }

    [Fact]
    public void More_engine_hours_than_elapsed_time_are_refused_with_SS101()
    {
        // +460 h within one day.
        var result = Lexion().CheckReading(900, Now, Last(440, Now.AddDays(-1)), Now);

        result.Error.Code.ShouldBe("SS101");
    }

    [Fact]
    public void Exactly_the_elapsed_hours_are_still_plausible()
    {
        Lexion().CheckReading(464, Now, Last(440, Now.AddHours(-24)), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_reading_of_an_inactive_machine_is_refused_with_SS102()
    {
        Lexion(MachineStatus.Decommissioned).CheckReading(512, Now, null, Now).Error.Code.ShouldBe("SS102");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_reading_not_after_the_last_one_is_refused_with_SS103(int minutesAfterLast)
    {
        var last = Last(440, Now.AddDays(-1));

        var result = Lexion().CheckReading(450, last.ReadAt.AddMinutes(minutesAfterLast), last, Now);

        result.Error.Code.ShouldBe("SS103");
    }

    [Fact]
    public void A_reading_in_the_future_is_refused_beyond_the_clock_tolerance()
    {
        var machine = Lexion();

        machine.CheckReading(512, Now.AddMinutes(4), null, Now).IsSuccess.ShouldBeTrue();
        machine.CheckReading(512, Now.AddMinutes(6), null, Now).Error.ShouldBe(FleetErrors.ReadingInFuture);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100000.1)]
    [InlineData(512.25)]
    public void Engine_hours_outside_the_column_range_or_precision_are_refused(double hours)
    {
        Lexion().CheckReading((decimal)hours, Now, null, Now).Error.ShouldBe(FleetErrors.InvalidEngineHours);
    }

    [Fact]
    public void Engine_hours_print_with_one_decimal()
    {
        EngineHours.Create(512).Value.ToString().ShouldBe("512.0");
    }
}
