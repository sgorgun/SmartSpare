using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Fleet;

/// <summary>Service order statuses as the API names them (values of <c>status</c> fields).</summary>
public static class ServiceOrderStatuses
{
    public const string Draft = "draft";
    public const string PartsReserved = "partsReserved";
    public const string InProgress = "inProgress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Draft, PartsReserved, InProgress, Completed, Cancelled];
}

/// <summary>Where the kit of a service order stands (values of <c>kitStatus</c>).</summary>
public static class KitStatuses
{
    /// <summary>Drafted; Inventory is reserving the kit (the answer to ServiceOrderDrafted is pending).</summary>
    public const string Reserving = "reserving";

    /// <summary>Every part is held at the hub.</summary>
    public const string Reserved = "reserved";

    /// <summary>Some parts are held, some are short.</summary>
    public const string Partial = "partial";

    /// <summary>Nothing is held: no stock, or the holds expired.</summary>
    public const string NotHeld = "notHeld";

    /// <summary>The order is completed and its parts were issued.</summary>
    public const string Issued = "issued";

    /// <summary>The order is cancelled and its holds released.</summary>
    public const string Released = "released";

    /// <summary>The order has no parts.</summary>
    public const string NoParts = "noParts";
}

/// <summary>What the current user may do with a service order (values of <c>allowedActions</c>).</summary>
public static class ServiceOrderActions
{
    public const string Start = "start";
    public const string Complete = "complete";
    public const string Cancel = "cancel";

    /// <summary>Change the kit lines (PUT /service-orders/{id}/lines), while the work has not started.</summary>
    public const string EditLines = "editLines";
}

/// <summary>One service order in a list.</summary>
[Description("One service order in a list.")]
public sealed record ServiceOrderSummary(
    [property: Description("Service order id.")] long Id,
    [property: Description("Number, SO-YYYY-NNNNNN.")] string Number,
    [property: Description("Machine id.")] long MachineId,
    [property: Description("Machine VIN.")] string Vin,
    [property: Description("Model name.")] string ModelName,
    [property: Description("Maintenance plan name; null for an unplanned repair.")] string? PlanName,
    [property: Description("Hub that supplies the kit.")] short WarehouseId,
    [property: Description("Hub code.")] string WarehouseCode,
    [property: Description("One of ServiceOrderStatuses.")] string Status,
    [property: Description("One of KitStatuses.")] string KitStatus,
    [property: Description("Engine-hour threshold of a planned service; null for a repair.")] decimal? DueEngineHours,
    [property: Description("Time the order was drafted.")] DateTimeOffset CreatedAt,
    [property: Description("Time the order was completed, if it was.")] DateTimeOffset? CompletedAt);

/// <summary>One part of a service order.</summary>
[Description("One part of a service order: required, held at the hub, issued and still missing.")]
public sealed record ServiceOrderLineResponse(
    [property: Description("Line id.")] long Id,
    [property: Description("Part id.")] long PartId,
    [property: Description("Manufacturer part number; null if unknown to Fleet's catalogue copy.")] string? OemPartNumber,
    [property: Description("Part description; null if unknown.")] string? Description,
    [property: Description("Unit of measure code; null if unknown.")] string? UnitOfMeasure,
    [property: Description("Quantity the work needs.")] decimal QtyRequired,
    [property: Description("Quantity held for the line at the hub.")] decimal QtyReserved,
    [property: Description("Quantity issued from stock.")] decimal QtyIssued,
    [property: Description("Required minus issued minus held.")] decimal QtyMissing);

/// <summary>A service order with its kit and the actions the current user may take.</summary>
[Description("A service order with its kit, its history and the actions the current user may take.")]
public sealed record ServiceOrderDetails(
    [property: Description("The order.")] ServiceOrderSummary Order,
    [property: Description("Engine hours when the order was drafted.")] decimal? TriggerEngineHours,
    [property: Description("Engine hours at completion.")] decimal? CompletedEngineHours,
    [property: Description("Why the order was cancelled.")] string? CancelReason,
    [property: Description("User who drafted the order.")] Guid? CreatedBy,
    [property: Description("When Inventory answered the kit reservation; null while it is pending.")] DateTimeOffset? KitReservedAt,
    [property: Description("The kit.")] IReadOnlyList<ServiceOrderLineResponse> Lines,
    [property: Description("Actions the current user may take now: values of ServiceOrderActions.")] IReadOnlyList<string> AllowedActions);

/// <summary>A part of an unplanned repair.</summary>
[Description("A part of an unplanned repair.")]
public sealed record CreateServiceOrderLine(
    [property: Description("Part id (from Fleet's catalogue copy).")] long PartId,
    [property: Description("Quantity, greater than zero, at most three decimals.")] decimal Quantity);

/// <summary>A new service order: a planned service of a maintenance plan, or an unplanned repair with its parts.</summary>
[Description("A new service order: maintenancePlanId for a planned service (kit from the plan), or lines for an unplanned repair.")]
public sealed record CreateServiceOrderRequest(
    [property: Description("Machine id.")] long MachineId,
    [property: Description("Maintenance plan of the machine's model; empty for a repair.")] int? MaintenancePlanId = null,
    [property: Description("Parts of a repair; empty for a planned service.")] IReadOnlyList<CreateServiceOrderLine>? Lines = null);

/// <summary>Rules of <see cref="CreateServiceOrderRequest"/>, shared by the form and the server.</summary>
public sealed class CreateServiceOrderRequestValidator : AbstractValidator<CreateServiceOrderRequest>
{
    public const int MaxLines = 50;

    public CreateServiceOrderRequestValidator()
    {
        RuleFor(r => r.MachineId).GreaterThan(0);
        RuleFor(r => r).Must(r => (r.MaintenancePlanId is not null) ^ (r.Lines is { Count: > 0 }))
            .WithName("Order").WithMessage("Give either a maintenance plan or the parts of a repair, not both.");
        RuleFor(r => r.Lines!.Count).LessThanOrEqualTo(MaxLines).When(r => r.Lines is not null).WithName("Lines");
        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PartId).GreaterThan(0);
            line.RuleFor(l => l.Quantity).GreaterThan(0).LessThanOrEqualTo(999_999_999.999m)
                .Must(q => decimal.Round(q, 3) == q).WithMessage("A quantity has at most three decimals.");
        });
        RuleFor(r => r.Lines).Must(l => l!.Select(x => x.PartId).Distinct().Count() == l!.Count).When(r => r.Lines is not null)
            .WithMessage("Each part appears only once.");
    }
}

/// <summary>The completion of a service order.</summary>
[Description("The completion of a service order: the machine's engine hours when the work was done.")]
public sealed record CompleteServiceOrderRequest(
    [property: Description("Engine hours at completion, 0 to 100,000 with at most one decimal; not below the hours that triggered the order or the machine's current hours.")] decimal EngineHours);

/// <summary>Rules of <see cref="CompleteServiceOrderRequest"/>, shared by the form and the server.</summary>
public sealed class CompleteServiceOrderRequestValidator : AbstractValidator<CompleteServiceOrderRequest>
{
    public CompleteServiceOrderRequestValidator() =>
        RuleFor(r => r.EngineHours).InclusiveBetween(0, RecordReadingRequestValidator.MaxHours)
            .Must(h => decimal.Round(h, 1) == h).WithMessage("Engine hours have at most one decimal.");
}

/// <summary>The cancellation of a service order.</summary>
[Description("The cancellation of a service order.")]
public sealed record CancelServiceOrderRequest(
    [property: Description("Why the order is cancelled, 1 to 300 characters.")] string Reason);

/// <summary>Rules of <see cref="CancelServiceOrderRequest"/>, shared by the form and the server.</summary>
public sealed class CancelServiceOrderRequestValidator : AbstractValidator<CancelServiceOrderRequest>
{
    public const int MaxReasonLength = 300;

    public CancelServiceOrderRequestValidator() =>
        RuleFor(r => r.Reason).Must(r => !string.IsNullOrWhiteSpace(r)).WithMessage("A cancellation needs a reason.")
            .MaximumLength(MaxReasonLength);
}

/// <summary>The kit an open service order should have from now on.</summary>
[Description("The kit an open service order should have from now on: every part once with its quantity.")]
public sealed record ChangeServiceOrderLinesRequest(
    [property: Description("All lines of the kit. Missing parts are removed, new parts added, changed quantities replaced; empty is allowed for a planned service only.")] IReadOnlyList<CreateServiceOrderLine> Lines);

/// <summary>Rules of <see cref="ChangeServiceOrderLinesRequest"/>, shared by the form and the server.</summary>
public sealed class ChangeServiceOrderLinesRequestValidator : AbstractValidator<ChangeServiceOrderLinesRequest>
{
    public ChangeServiceOrderLinesRequestValidator()
    {
        RuleFor(r => r.Lines).NotNull();
        RuleFor(r => r.Lines.Count).LessThanOrEqualTo(CreateServiceOrderRequestValidator.MaxLines).When(r => r.Lines is not null).WithName("Lines");
        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PartId).GreaterThan(0);
            line.RuleFor(l => l.Quantity).GreaterThan(0).LessThanOrEqualTo(999_999_999.999m)
                .Must(q => decimal.Round(q, 3) == q).WithMessage("A quantity has at most three decimals.");
        });
        RuleFor(r => r.Lines).Must(l => l.Select(x => x.PartId).Distinct().Count() == l.Count).When(r => r.Lines is not null)
            .WithMessage("Each part appears only once.");
    }
}
