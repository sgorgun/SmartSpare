using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Machines;
using SmartSpare.Modules.Fleet.Features.Readings;

namespace SmartSpare.Modules.Fleet;

/// <summary>The rules of recording a reading that need no database: input validation, sources, precision and SS104.</summary>
public sealed class ReadingFeatureTests
{
    [Fact]
    public void The_command_accepts_valid_hours_and_the_api_sources()
    {
        var validator = new RecordReadingCommandValidator();

        validator.Validate(new RecordReadingCommand(1, 512.5m, null, null)).IsValid.ShouldBeTrue();
        foreach (var source in new[] { ReadingSources.Manual, ReadingSources.Telematics, ReadingSources.ServiceVisit })
        {
            validator.Validate(new RecordReadingCommand(1, 512, null, source)).IsValid.ShouldBeTrue(source);
        }
    }

    [Theory]
    [InlineData(-1, null, "Hours")]
    [InlineData(100000.1, null, "Hours")]
    [InlineData(512.25, null, "Hours")]
    [InlineData(512, "import", "Source")]
    [InlineData(512, "odometer", "Source")]
    public void The_command_refuses_invalid_hours_and_sources(double hours, string? source, string field)
    {
        var errors = new RecordReadingCommandValidator().Validate(new RecordReadingCommand(1, (decimal)hours, null, source)).Errors;

        errors.ShouldHaveSingleItem().PropertyName.ShouldBe(field);
    }

    [Fact]
    public void The_form_validator_shares_the_rules()
    {
        new RecordReadingRequestValidator().Validate(new RecordReadingRequest(512.25m)).IsValid.ShouldBeFalse();
        new RecordReadingRequestValidator().Validate(new RecordReadingRequest(512, Source: "import")).IsValid.ShouldBeFalse();
        new RecordReadingRequestValidator().Validate(new RecordReadingRequest(512, DateTimeOffset.UtcNow, "telematics")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Sources_map_between_the_api_and_the_domain()
    {
        ReadingSourceNames.Parse(null).ShouldBe(ReadingSource.Manual);
        ReadingSourceNames.Parse("manual").ShouldBe(ReadingSource.Manual);
        ReadingSourceNames.Parse("telematics").ShouldBe(ReadingSource.Telematics);
        ReadingSourceNames.Parse("serviceVisit").ShouldBe(ReadingSource.ServiceVisit);
        ReadingSourceNames.Parse("import").ShouldBeNull();

        ReadingSourceNames.Name(ReadingSource.ServiceVisit).ShouldBe("serviceVisit");
        ReadingSourceNames.Name(ReadingSource.Import).ShouldBe("import");
        ReadingSourceNames.Name(ReadingSource.Telematics).ShouldBe("telematics");
        ReadingSourceNames.Name(ReadingSource.Manual).ShouldBe("manual");
    }

    [Fact]
    public void Reading_times_are_cut_to_the_microseconds_postgresql_keeps()
    {
        var time = new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var stored = ReadingSourceNames.ToDatabasePrecision(time);

        stored.Ticks.ShouldBe(time.Ticks - 7);
        ReadingSourceNames.ToDatabasePrecision(stored).ShouldBe(stored);
    }

    [Fact]
    public void A_reading_in_the_future_is_SS104_and_a_different_value_at_the_same_time_a_conflict()
    {
        FleetErrors.ReadingInFuture.Code.ShouldBe("SS104");
        FleetErrors.ReadingInFuture.Type.ShouldBe(SharedKernel.ErrorType.BusinessRule);
        FleetErrors.ReadingConflict.Type.ShouldBe(SharedKernel.ErrorType.Conflict);
    }
}
