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
