using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Fleet;

/// <summary>An engine-hour reading entered by an engineer or a feed (UC02 steps 1–3).</summary>
[Description("An engine-hour reading of a machine.")]
public sealed record RecordReadingRequest(
    [property: Description("Engine hours on the odometer, 0 to 100,000 with at most one decimal.")] decimal Hours,
    [property: Description("Time the reading was taken; empty means now. At most five minutes ahead of the server clock.")] DateTimeOffset? ReadAt = null,
    [property: Description("manual (default), telematics or serviceVisit; imports use the import API.")] string? Source = null);

/// <summary>A service order drafted because the reading made a planned service due (US05).</summary>
[Description("A service order drafted because the reading made a planned service due.")]
public sealed record DraftedServiceOrder(
    [property: Description("Service order id.")] long Id,
    [property: Description("Service order number, SO-YYYY-NNNNNN.")] string Number,
    [property: Description("Engine-hour threshold the planned service covers.")] decimal DueEngineHours,
    [property: Description("Whether the whole kit could be reserved at the machine's home hub.")] bool FullyReserved);

/// <summary>The outcome of recording a reading.</summary>
[Description("The stored reading, the machine's engine hours and the service orders the reading drafted.")]
public sealed record RecordReadingResponse(
    [property: Description("The stored reading.")] ReadingRow Reading,
    [property: Description("The machine's engine hours after the reading.")] decimal EngineHours,
    [property: Description("True when an identical reading already existed (a retry); nothing was stored or drafted again.")] bool Replayed,
    [property: Description("Service orders drafted by this reading: every plan due within 50 h (US05).")] IReadOnlyList<DraftedServiceOrder> DraftedOrders);

/// <summary>Rules of <see cref="RecordReadingRequest"/>, shared by the form and the server.</summary>
public sealed class RecordReadingRequestValidator : AbstractValidator<RecordReadingRequest>
{
    public const decimal MaxHours = 100_000m;

    /// <summary>Sources an API client may set; imports have their own pipeline.</summary>
    public static IReadOnlyList<string> AllowedSources { get; } = [ReadingSources.Manual, ReadingSources.Telematics, ReadingSources.ServiceVisit];

    public RecordReadingRequestValidator()
    {
        RuleFor(r => r.Hours).InclusiveBetween(0, MaxHours)
            .Must(h => decimal.Round(h, 1) == h).WithMessage("Engine hours have at most one decimal.");
        RuleFor(r => r.Source).Must(s => AllowedSources.Contains(s!)).When(r => r.Source is not null)
            .WithMessage($"'Source' must be one of: {string.Join(", ", AllowedSources)}.");
    }
}
