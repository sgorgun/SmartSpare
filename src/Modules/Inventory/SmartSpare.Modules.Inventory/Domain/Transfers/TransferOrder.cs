using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Transfers;

/// <summary>PostgreSQL enum <c>inventory.transfer_status</c>.</summary>
internal enum TransferStatus
{
    Proposed,
    Approved,
    Dispatched,
    Received,
    Cancelled,
}

/// <summary>Input for a transfer line: the part, the quantity to move and, for a system proposal, what was proposed.</summary>
internal sealed record TransferLineDraft(long PartId, decimal Quantity, decimal? ProposedQty = null, string? OverrideReason = null);

/// <summary>
/// An inter-warehouse transfer order (aggregate root, ADR 0013). The state machine equals the trigger
/// <c>inventory.trg_transfer_order_status_guard</c>: proposed → approved | cancelled; approved → dispatched | cancelled;
/// dispatched → received. Lines change only while the order is proposed (<c>trg_transfer_line_editable</c>).
/// Approval, dispatch, receipt and cancellation are written by the SQL functions (reservations, ledger, locks); the
/// methods here decide whether the step is allowed and give the readable error first (STAGE_5_PLAN section 2.1).
/// </summary>
internal sealed class TransferOrder : AggregateRoot<long>
{
    /// <summary>Length of <c>cancel_reason</c> and <c>override_reason</c>.</summary>
    public const int MaxReasonLength = 300;

    private readonly List<TransferOrderLine> _lines = [];

    private TransferOrder()
        : base(0)
    {
        Number = null!;
    }

    private TransferOrder(short fromWarehouseId, short toWarehouseId, bool isUrgent, Guid requestedBy)
        : base(0)
    {
        // Assigned by the database trigger on insert (util.trg_assign_document_number): TR-YYYY-NNNNNN.
        Number = null!;
        FromWarehouseId = fromWarehouseId;
        ToWarehouseId = toWarehouseId;
        IsUrgent = isUrgent;
        RequestedBy = requestedBy;
        Status = TransferStatus.Proposed;
    }

    public string Number { get; private set; }

    public short FromWarehouseId { get; private set; }

    public short ToWarehouseId { get; private set; }

    public TransferStatus Status { get; private set; }

    public bool IsUrgent { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? DispatchedBy { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public Guid? ReceivedBy { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Maintained by the <c>updated_at</c> trigger.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<TransferOrderLine> Lines => _lines.AsReadOnly();

    /// <summary>The parts of all lines: the input of the brand scope check (STAGE_3_PLAN 3.3).</summary>
    public IReadOnlySet<long> PartIds => _lines.Select(l => l.PartId).ToHashSet();

    /// <summary>Proposes a transfer between two hubs with at least one line, each part once.</summary>
    public static Result<TransferOrder> Propose(
        short fromWarehouseId, short toWarehouseId, bool isUrgent, Guid requestedBy, IReadOnlyCollection<TransferLineDraft> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (fromWarehouseId == toWarehouseId)
        {
            return InventoryErrors.SameWarehouse;
        }

        if (lines.Count == 0)
        {
            return InventoryErrors.NoLines;
        }

        var order = new TransferOrder(fromWarehouseId, toWarehouseId, isUrgent, requestedBy);
        foreach (var draft in lines)
        {
            var added = order.AddLine(draft);
            if (added.IsFailure)
            {
                return added.Error;
            }
        }

        return order;
    }

    /// <summary>Adds a line to a proposed order; a part may appear only once.</summary>
    public Result AddLine(TransferLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (Status != TransferStatus.Proposed)
        {
            return InventoryErrors.InvalidTransition("transfer order", Status, "changed");
        }

        if (_lines.Any(l => l.PartId == draft.PartId))
        {
            return InventoryErrors.DuplicatePart;
        }

        var line = TransferOrderLine.Create(draft);
        if (line.IsFailure)
        {
            return line.Error;
        }

        _lines.Add(line.Value);
        return Result.Success();
    }

    /// <summary>Changes the quantity of a line of a proposed order; a change from the proposal needs a reason (UC01 A2).</summary>
    public Result ChangeLineQuantity(long partId, decimal quantity, string? overrideReason)
    {
        if (Status != TransferStatus.Proposed)
        {
            return InventoryErrors.InvalidTransition("transfer order", Status, "changed");
        }

        var line = _lines.Find(l => l.PartId == partId);
        return line is null ? InventoryErrors.LineNotFound : line.ChangeQuantity(quantity, overrideReason);
    }

    /// <summary>Removes a line of a proposed order; the last line cannot be removed (cancel the order instead).</summary>
    public Result RemoveLine(long partId)
    {
        if (Status != TransferStatus.Proposed)
        {
            return InventoryErrors.InvalidTransition("transfer order", Status, "changed");
        }

        var line = _lines.Find(l => l.PartId == partId);
        if (line is null)
        {
            return InventoryErrors.LineNotFound;
        }

        if (_lines.Count == 1)
        {
            return InventoryErrors.NoLines;
        }

        _lines.Remove(line);
        return Result.Success();
    }

    /// <summary>UC01 steps 10–11: a proposed order with lines is approved (the SQL function places the firm reservations).</summary>
    public Result Approve(Guid userId, DateTimeOffset now)
    {
        var allowed = CanApprove();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = TransferStatus.Approved;
        ApprovedBy = userId;
        ApprovedAt = now;
        Raise(new TransferApprovedDomainEvent(Id, now));
        return Result.Success();
    }

    /// <summary>US11: an approved order is dispatched by a keeper of the donor hub (or a user without a home hub).</summary>
    public Result Dispatch(Guid userId, short? homeWarehouseId, DateTimeOffset now)
    {
        var allowed = CanDispatch(homeWarehouseId);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = TransferStatus.Dispatched;
        DispatchedBy = userId;
        DispatchedAt = now;
        return Result.Success();
    }

    /// <summary>US11: a dispatched order is received by a keeper of the recipient hub (or a user without a home hub).</summary>
    public Result Receive(Guid userId, short? homeWarehouseId, DateTimeOffset now)
    {
        var allowed = CanReceive(homeWarehouseId);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = TransferStatus.Received;
        ReceivedBy = userId;

        // The SQL function never records a receipt before the dispatch (ck_transfer_order_timeline2).
        ReceivedAt = DispatchedAt is { } dispatched && dispatched > now ? dispatched : now;
        return Result.Success();
    }

    /// <summary>A proposed or approved order is cancelled with a reason (firm reservations are released by the SQL function).</summary>
    public Result Cancel(string? reason, DateTimeOffset now)
    {
        var allowed = CanCancel();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
        {
            return InventoryErrors.CancelReasonRequired;
        }

        Status = TransferStatus.Cancelled;
        CancelReason = reason.Trim();
        Raise(new TransferCancelledDomainEvent(Id, now));
        return Result.Success();
    }

    public Result CanApprove() =>
        Status != TransferStatus.Proposed ? InventoryErrors.InvalidTransition("transfer order", Status, "approved")
        : _lines.Count == 0 ? InventoryErrors.NoLines
        : Result.Success();

    public Result CanDispatch(short? homeWarehouseId) =>
        Status != TransferStatus.Approved ? InventoryErrors.InvalidTransition("transfer order", Status, "dispatched")
        : homeWarehouseId is { } home && home != FromWarehouseId ? InventoryErrors.NotYourWarehouse
        : Result.Success();

    public Result CanReceive(short? homeWarehouseId) =>
        Status != TransferStatus.Dispatched ? InventoryErrors.InvalidTransition("transfer order", Status, "received")
        : homeWarehouseId is { } home && home != ToWarehouseId ? InventoryErrors.NotYourWarehouse
        : Result.Success();

    public Result CanCancel() =>
        Status is TransferStatus.Proposed or TransferStatus.Approved
            ? Result.Success()
            : InventoryErrors.InvalidTransition("transfer order", Status, "cancelled");
}

/// <summary>Raised when a transfer order is approved.</summary>
internal sealed record TransferApprovedDomainEvent(long TransferOrderId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a transfer order is cancelled.</summary>
internal sealed record TransferCancelledDomainEvent(long TransferOrderId, DateTimeOffset OccurredAt) : IDomainEvent;
