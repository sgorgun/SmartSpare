using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain.ServiceOrders;

/// <summary>PostgreSQL enum <c>fleet.service_order_status</c>.</summary>
internal enum ServiceOrderStatus
{
    Draft,
    PartsReserved,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>
/// A service order (aggregate root, ADR 0013): a planned service of a maintenance plan at an engine-hour threshold, or
/// an unplanned repair. The state machine equals the trigger <c>fleet.trg_service_order_status_guard</c>:
/// draft → parts_reserved | in_progress | completed | cancelled; parts_reserved → draft | in_progress | completed |
/// cancelled; in_progress → completed | cancelled. Drafting from a plan, completion and cancellation are written by the
/// SQL functions; the methods here decide whether the step is allowed and give the readable error first
/// (STAGE_6_PLAN section 3).
/// </summary>
internal sealed class ServiceOrder : AggregateRoot<long>
{
    /// <summary>Length of <c>cancel_reason</c>.</summary>
    public const int MaxReasonLength = 300;

    private readonly List<ServiceOrderLine> _lines = [];

    private ServiceOrder()
        : base(0)
    {
        Number = null!;
    }

    private ServiceOrder(long machineId, int? maintenancePlanId, short warehouseId, decimal? dueEngineHours,
        decimal? triggerEngineHours, Guid createdBy)
        : base(0)
    {
        // Assigned by the database trigger on insert (util.trg_assign_document_number): SO-YYYY-NNNNNN.
        Number = null!;
        MachineId = machineId;
        MaintenancePlanId = maintenancePlanId;
        WarehouseId = warehouseId;
        DueEngineHours = dueEngineHours;
        TriggerEngineHours = triggerEngineHours;
        CreatedBy = createdBy;
        Status = ServiceOrderStatus.Draft;
    }

    public string Number { get; private set; }

    public long MachineId { get; private set; }

    /// <summary>The plan of a planned service; null for an unplanned repair.</summary>
    public int? MaintenancePlanId { get; private set; }

    /// <summary>The hub that supplies the kit (the machine's home hub).</summary>
    public short WarehouseId { get; private set; }

    public ServiceOrderStatus Status { get; private set; }

    /// <summary>The engine-hour threshold this planned service covers (500, 1000, …).</summary>
    public decimal? DueEngineHours { get; private set; }

    /// <summary>Engine hours of the machine when the order was drafted.</summary>
    public decimal? TriggerEngineHours { get; private set; }

    public decimal? CompletedEngineHours { get; private set; }

    public Guid? CreatedBy { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>Maintained by the <c>updated_at</c> trigger.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ServiceOrderLine> Lines => _lines.AsReadOnly();

    public bool IsPlanned => MaintenancePlanId is not null;

    /// <summary>Draft, parts reserved or in progress.</summary>
    public bool IsOpen => Status is ServiceOrderStatus.Draft or ServiceOrderStatus.PartsReserved or ServiceOrderStatus.InProgress;

    /// <summary>
    /// US06 / UC02 steps 5–6: a planned service of <paramref name="planId"/> at threshold <paramref name="dueEngineHours"/>
    /// with the plan's kit. The SQL function <c>fleet.create_service_order_draft</c> writes it; this factory states the
    /// same rules for orders built in C#.
    /// </summary>
    public static Result<ServiceOrder> DraftFromPlan(long machineId, int planId, short warehouseId, decimal dueEngineHours,
        decimal triggerEngineHours, Guid createdBy, IReadOnlyCollection<ServiceOrderLineDraft> kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (EngineHours.Create(dueEngineHours).IsFailure || dueEngineHours <= 0)
        {
            return FleetErrors.InvalidEngineHours;
        }

        var order = new ServiceOrder(machineId, planId, warehouseId, dueEngineHours, triggerEngineHours, createdBy);
        return order.AddLines(kit);
    }

    /// <summary>An unplanned repair with the parts the engineer expects to need (at least one).</summary>
    public static Result<ServiceOrder> DraftRepair(long machineId, short warehouseId, decimal machineEngineHours, Guid createdBy,
        IReadOnlyCollection<ServiceOrderLineDraft> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return FleetErrors.NoLines;
        }

        var order = new ServiceOrder(machineId, null, warehouseId, null, machineEngineHours, createdBy);
        return order.AddLines(lines);
    }

    /// <summary>Adds a part to an order that has not started; each part once.</summary>
    public Result AddLine(ServiceOrderLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var editable = CanChangeLines();
        if (editable.IsFailure)
        {
            return editable;
        }

        if (_lines.Exists(l => l.PartId == draft.PartId))
        {
            return FleetErrors.DuplicatePart;
        }

        var line = ServiceOrderLine.Create(draft);
        if (line.IsFailure)
        {
            return line.Error;
        }

        _lines.Add(line.Value);
        return Result.Success();
    }

    /// <summary>Changes the quantity of a line of an order that has not started.</summary>
    public Result ChangeLineQuantity(long partId, decimal quantity)
    {
        var editable = CanChangeLines();
        if (editable.IsFailure)
        {
            return editable;
        }

        var line = _lines.Find(l => l.PartId == partId);
        return line is null ? FleetErrors.LineNotFound : line.ChangeQuantity(quantity);
    }

    /// <summary>Removes a line of an order that has not started; a repair keeps at least one line (cancel instead).</summary>
    public Result RemoveLine(long partId)
    {
        var editable = CanChangeLines();
        if (editable.IsFailure)
        {
            return editable;
        }

        var line = _lines.Find(l => l.PartId == partId);
        if (line is null)
        {
            return FleetErrors.LineNotFound;
        }

        if (!IsPlanned && _lines.Count == 1)
        {
            return FleetErrors.NoLines;
        }

        _lines.Remove(line);
        return Result.Success();
    }

    /// <summary>The engineer starts the work.</summary>
    public Result Start()
    {
        var allowed = CanStart();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = ServiceOrderStatus.InProgress;
        return Result.Success();
    }

    /// <summary>
    /// The work is done at <paramref name="engineHours"/>: not below the hours that triggered the order and not below the
    /// machine's current hours (a later reading would otherwise contradict the service record).
    /// </summary>
    public Result Complete(decimal engineHours, decimal machineEngineHours, DateTimeOffset now)
    {
        var allowed = CanComplete();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var hours = EngineHours.Create(engineHours);
        if (hours.IsFailure)
        {
            return hours.Error;
        }

        if (engineHours < (TriggerEngineHours ?? 0) || engineHours < machineEngineHours)
        {
            return FleetErrors.CompletionHoursTooLow;
        }

        Status = ServiceOrderStatus.Completed;
        CompletedEngineHours = engineHours;
        CompletedAt = now;
        Raise(new ServiceOrderCompletedDomainEvent(Id, now));
        return Result.Success();
    }

    /// <summary>An open order is cancelled with a reason.</summary>
    public Result Cancel(string? reason, DateTimeOffset now)
    {
        var allowed = CanCancel();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
        {
            return FleetErrors.CancelReasonRequired;
        }

        Status = ServiceOrderStatus.Cancelled;
        CancelReason = reason.Trim();
        Raise(new ServiceOrderCancelledDomainEvent(Id, now));
        return Result.Success();
    }

    public Result CanChangeLines() =>
        Status is ServiceOrderStatus.Draft or ServiceOrderStatus.PartsReserved
            ? Result.Success()
            : FleetErrors.InvalidTransition(Status, "changed");

    public Result CanStart() =>
        Status is ServiceOrderStatus.Draft or ServiceOrderStatus.PartsReserved
            ? Result.Success()
            : FleetErrors.InvalidTransition(Status, "started");

    public Result CanComplete() =>
        IsOpen ? Result.Success() : FleetErrors.InvalidTransition(Status, "completed");

    public Result CanCancel() =>
        IsOpen ? Result.Success() : FleetErrors.InvalidTransition(Status, "cancelled");

    private Result<ServiceOrder> AddLines(IEnumerable<ServiceOrderLineDraft> lines)
    {
        foreach (var draft in lines)
        {
            var added = AddLine(draft);
            if (added.IsFailure)
            {
                return added.Error;
            }
        }

        return this;
    }
}

/// <summary>Raised when a service order is completed.</summary>
internal sealed record ServiceOrderCompletedDomainEvent(long ServiceOrderId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a service order is cancelled.</summary>
internal sealed record ServiceOrderCancelledDomainEvent(long ServiceOrderId, DateTimeOffset OccurredAt) : IDomainEvent;
