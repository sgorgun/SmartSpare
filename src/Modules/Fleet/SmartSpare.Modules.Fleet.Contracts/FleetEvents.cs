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
