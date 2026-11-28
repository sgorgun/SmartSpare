using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Contracts;

/// <summary>A part and quantity a service order needs, as carried by the service order events.</summary>
public sealed record ServiceOrderKitLine(long LineId, long PartId, decimal Quantity);

/// <summary>
/// A service order was drafted with its kit (UC02 steps 5–6, US06; MODULE_MAPPING X6). Inventory reserves the kit at
/// <see cref="WarehouseId"/> and answers with <c>ServiceKitReserved</c>. Written to Fleet's outbox in the draft's
/// transaction from PR 6g2, when Inventory consumes it (STAGE_6_PLAN 5.1, 5.3). Handled as <see cref="ActorId"/>.
/// </summary>
public sealed record ServiceOrderDrafted(
    long ServiceOrderId,
    string Number,
    short WarehouseId,
    IReadOnlyList<ServiceOrderKitLine> Lines,
    Guid ActorId,
    DateTimeOffset DraftedAt) : IntegrationEvent, IActedByUser;

/// <summary>
/// A service order was completed (MODULE_MAPPING X7, STAGE_6_PLAN 5.4): Inventory consumes the holds and issues the open
/// quantities, the unreserved rest up to the available stock (F7), and answers with <c>ServiceKitIssued</c>. Written to
/// Fleet's outbox in the completion's transaction from PR 6h2. Handled as <see cref="ActorId"/>.
/// </summary>
public sealed record ServiceOrderCompleted(
    long ServiceOrderId,
    string Number,
    short WarehouseId,
    IReadOnlyList<ServiceOrderKitLine> Lines,
    Guid ActorId,
    DateTimeOffset CompletedAt) : IntegrationEvent, IActedByUser;

/// <summary>
/// A service order was cancelled (MODULE_MAPPING X8, STAGE_6_PLAN 5.5): Inventory releases the holds of its lines.
/// Written to Fleet's outbox in the cancellation's transaction from PR 6h3. Handled as <see cref="ActorId"/>.
/// </summary>
public sealed record ServiceOrderCancelled(
    long ServiceOrderId,
    string Number,
    IReadOnlyList<long> LineIds,
    string Reason,
    Guid ActorId,
    DateTimeOffset CancelledAt) : IntegrationEvent, IActedByUser;

/// <summary>
/// Inventory reserved a kit for an order that was already closed (its <c>ServiceKitReserved</c> arrived after a completion
/// or cancellation, STAGE_6_PLAN 5.5): Inventory releases those holds at once. Written by Fleet's consumer of
/// <c>ServiceKitReserved</c> from PR 6h3. Handled as <see cref="ActorId"/>, the user whose draft caused the holds.
/// </summary>
public sealed record ServiceKitReleaseRequested(long ServiceOrderId, IReadOnlyList<long> LineIds, Guid ActorId) : IntegrationEvent, IActedByUser;

/// <summary>
/// The kit of an open service order was edited (PR 6i, #140; MODULE_MAPPING X8): Inventory releases the holds of
/// <see cref="RemovedLineIds"/> and reserves <see cref="AddedLines"/> at <see cref="WarehouseId"/>, answering with
/// <c>ServiceKitReserved</c> for the added lines. A changed quantity arrives as a removed line plus an added one, so the
/// per-line idempotency of the reservation holds. Handled as <see cref="ActorId"/>.
/// </summary>
public sealed record ServiceOrderLinesChanged(
    long ServiceOrderId,
    short WarehouseId,
    IReadOnlyList<ServiceOrderKitLine> AddedLines,
    IReadOnlyList<long> RemovedLineIds,
    Guid ActorId,
    DateTimeOffset ChangedAt) : IntegrationEvent, IActedByUser;

/// <summary>
/// A Fleet record that holds a hub, as Inventory projects it (ADR 0029): a service order (open = draft, parts reserved or
/// in progress) or a machine (open = active, the hub is its home hub).
/// </summary>
public sealed record HubReference(string RecordKind, long RecordId, short WarehouseId, bool IsOpen)
{
    public const string ServiceOrder = "service_order";

    public const string Machine = "machine";
}

/// <summary>
/// Fleet records that hold a hub were created or changed state (ADR 0029, PR 7j1): the current state of each record a write
/// touched, so Inventory's <c>inventory.fleet_hub_reference</c> keeps the newest state per record and the warehouse archive
/// guard sees open service orders and machines based at a hub. Written in the same transaction as the change;
/// <c>fleet.hub-reference-publish</c> sends every record, in chunks of at most <see cref="MaxReferences"/>.
/// </summary>
public sealed record HubReferencesChanged(IReadOnlyList<HubReference> References) : IntegrationEvent
{
    public const int MaxReferences = 500;
}
