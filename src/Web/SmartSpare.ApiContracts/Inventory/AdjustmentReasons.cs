using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.MasterData;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>The directions an adjustment reason allows.</summary>
public static class AdjustmentDirections
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Both = "BOTH";

    public static IReadOnlyList<string> All { get; } = [In, Out, Both];
}

/// <summary>An adjustment reason (catalogue, ADR 0025).</summary>
[Description("Why stock is corrected.")]
public sealed record AdjustmentReasonDto(
    [property: Description("Reason id.")] short Id,
    [property: Description("Business key, e.g. DAMAGED.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("IN (surpluses only), OUT (shortages only) or BOTH.")] string Direction,
    [property: Description("Whether an adjustment with this reason needs a note.")] bool RequiresNote,
    [property: Description("Archived reasons stay on past adjustments but cannot be chosen.")] bool IsArchived,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>Body of creating or changing an adjustment reason.</summary>
[Description("An adjustment reason to create or change.")]
public sealed record AdjustmentReasonWrite(
    [property: Description("Business key: 2 to 30 capital letters, digits or underscores. Fixed after creation except for an administrator.")] string Code,
    [property: Description("Display name, up to 200 characters.")] string Name,
    [property: Description("IN, OUT or BOTH.")] string Direction,
    [property: Description("Whether an adjustment with this reason needs a note.")] bool RequiresNote) : ICatalogWrite;

public sealed class AdjustmentReasonWriteValidator : AbstractValidator<AdjustmentReasonWrite>
{
    public AdjustmentReasonWriteValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches("^[A-Za-z0-9_]{2,30}$")
            .WithMessage("Use 2 to 30 letters, digits or underscores, e.g. DAMAGED.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Direction).Must(d => AdjustmentDirections.All.Contains(d?.ToUpperInvariant()))
            .WithMessage("Choose IN, OUT or BOTH.");
    }
}
