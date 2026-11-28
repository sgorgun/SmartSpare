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
    [property: Description("Whether the whole kit is reserved. Always false when the order is drafted: Inventory reserves the kit right after, asynchronously (ServiceKitReserved), and the service order then shows the held quantities.")] bool FullyReserved);

/// <summary>The outcome of recording a reading.</summary>
[Description("The stored reading, the machine's engine hours and the service orders the reading drafted.")]
public sealed record RecordReadingResponse(
    [property: Description("The stored reading.")] ReadingRow Reading,
    [property: Description("The machine's engine hours after the reading.")] decimal EngineHours,
    [property: Description("True when an identical reading already existed (a retry); nothing was stored or drafted again.")] bool Replayed,
    [property: Description("Service orders drafted by this reading: every plan due within 50 h (US05).")] IReadOnlyList<DraftedServiceOrder> DraftedOrders,
    [property: Description("Things the user should know although the reading was stored, for example Fleet.HubArchived: maintenance is due but no order could be drafted. Empty when there are none.")] IReadOnlyList<ReadingWarning> Warnings);

/// <summary>A warning about a stored reading (ADR 0029).</summary>
[Description("A warning about a stored reading: the reading was kept, but something did not happen.")]
public sealed record ReadingWarning(
    [property: Description("Warning code, for example Fleet.HubArchived.")] string Code,
    [property: Description("Readable explanation.")] string Message)
{
    /// <summary>Maintenance is due, but the machine's home hub is archived, so no service order was drafted.</summary>
    public const string HubArchived = "Fleet.HubArchived";
}

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
