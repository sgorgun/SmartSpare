using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>Values of <see cref="TransferSummary.Status"/>: the transfer order state machine.</summary>
public static class TransferStatuses
{
    public const string Proposed = "proposed";
    public const string Approved = "approved";
    public const string Dispatched = "dispatched";
    public const string Received = "received";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Proposed, Approved, Dispatched, Received, Cancelled];
}

/// <summary>Values of <see cref="TransferDetails.AllowedActions"/>: what the current user may do with the order now.</summary>
public static class TransferActions
{
    public const string Approve = "approve";
    public const string Cancel = "cancel";
    public const string Dispatch = "dispatch";
    public const string Receive = "receive";
}

/// <summary>Limits of the transfer requests and lists.</summary>
public static class TransferLimits
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxLines = 50;

    public const int MaxReasonLength = 300;
}

/// <summary>A transfer order in a list.</summary>
[Description("A transfer order in a list.")]
public sealed record TransferSummary(
    [property: Description("Transfer order id.")] long Id,
    [property: Description("Document number, for example TR-2026-000412.")] string Number,
    [property: Description("Donor hub id.")] short FromWarehouseId,
    [property: Description("Donor hub code.")] string FromWarehouseCode,
    [property: Description("Recipient hub id.")] short ToWarehouseId,
    [property: Description("Recipient hub code.")] string ToWarehouseCode,
    [property: Description("proposed, approved, dispatched, received or cancelled.")] string Status,
    [property: Description("Urgent transfers may use the donor's safety stock (US10).")] bool IsUrgent,
    [property: Description("Number of lines.")] int LineCount,
    [property: Description("Sum of the line quantities.")] decimal TotalQuantity,
    [property: Description("When the order was created.")] DateTimeOffset CreatedAt,
    [property: Description("When the order last changed.")] DateTimeOffset UpdatedAt);

/// <summary>A line of a transfer order.</summary>
[Description("A line of a transfer order.")]
public sealed record TransferLineResponse(
    [property: Description("Line id.")] long Id,
    [property: Description("Part id.")] long PartId,
    [property: Description("Brand code of the part.")] string BrandCode,
    [property: Description("Manufacturer part number.")] string OemPartNumber,
    [property: Description("Part description.")] string Description,
    [property: Description("Unit of measure code.")] string UnitOfMeasure,
    [property: Description("Quantity to move.")] decimal Quantity,
    [property: Description("Quantity proposed by the system, if the order came from a recommendation.")] decimal? ProposedQty,
    [property: Description("Why the quantity differs from the proposal.")] string? OverrideReason,
    [property: Description("Available quantity at the donor hub now.")] decimal DonorAvailable);

/// <summary>One step of a transfer order: who did it and when.</summary>
[Description("One step of a transfer order: who did it and when.")]
public sealed record TransferStep(
    [property: Description("requested, approved, dispatched, received or cancelled.")] string Step,
    [property: Description("Id of the user who took the step, if recorded.")] Guid? UserId,
    [property: Description("When the step was taken.")] DateTimeOffset? At);

/// <summary>A transfer order with its lines, its timeline and what the current user may do with it.</summary>
[Description("A transfer order with its lines, its timeline and the actions the current user may take.")]
public sealed record TransferDetails(
    [property: Description("The order as listed.")] TransferSummary Order,
    [property: Description("The lines, by OEM part number.")] IReadOnlyList<TransferLineResponse> Lines,
    [property: Description("Requested, approved, dispatched, received or cancelled, in order.")] IReadOnlyList<TransferStep> Timeline,
    [property: Description("Reason of a cancellation.")] string? CancelReason,
    [property: Description("Actions the current user may take now: approve, cancel, dispatch, receive.")] IReadOnlyList<string> AllowedActions);

/// <summary>A line of a new transfer.</summary>
[Description("A line of a new transfer.")]
public sealed record CreateTransferLine(
    [property: Description("Part id.")] long PartId,
    [property: Description("Quantity to move, greater than zero, at most three decimals.")] decimal Quantity);

/// <summary>A new transfer proposal between two hubs.</summary>
[Description("A new transfer proposal between two hubs.")]
public sealed record CreateTransferRequest(
    [property: Description("Donor hub id.")] short FromWarehouseId,
    [property: Description("Recipient hub id.")] short ToWarehouseId,
    [property: Description("Urgent transfers may use the donor's safety stock (US10).")] bool IsUrgent,
    [property: Description("Parts and quantities, each part once, 1 to 50 lines.")] IReadOnlyList<CreateTransferLine> Lines);

public sealed class CreateTransferRequestValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferRequestValidator()
    {
        RuleFor(r => r.FromWarehouseId).GreaterThan((short)0);
        RuleFor(r => r.ToWarehouseId).GreaterThan((short)0).NotEqual(r => r.FromWarehouseId)
            .WithMessage("The recipient hub must differ from the donor hub.");
        RuleFor(r => r.Lines).NotEmpty().Must(l => l is null || l.Count <= TransferLimits.MaxLines)
            .WithMessage($"A transfer has at most {TransferLimits.MaxLines} lines.")
            .Must(l => l is null || l.Select(x => x.PartId).Distinct().Count() == l.Count)
            .WithMessage("Each part appears only once in a transfer.");
        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PartId).GreaterThan(0);
            line.RuleFor(l => l.Quantity).GreaterThan(0).LessThan(InventoryLimits.MaxQuantity).PrecisionScale(12, 3, true);
        });
    }
}

/// <summary>Why a transfer is cancelled.</summary>
[Description("Why a transfer is cancelled.")]
public sealed record CancelTransferRequest(
    [property: Description("Reason, 1 to 300 characters; recorded on the order and in the audit trail.")] string Reason);

public sealed class CancelTransferRequestValidator : AbstractValidator<CancelTransferRequest>
{
    public CancelTransferRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(TransferLimits.MaxReasonLength);
    }
}
