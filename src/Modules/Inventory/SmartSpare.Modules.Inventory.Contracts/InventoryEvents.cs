using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Contracts;

/// <summary>A part and quantity of a transfer, as carried by the transfer events.</summary>
public sealed record TransferredPart(long PartId, decimal Quantity);

/// <summary>
/// A transfer order was approved and its stock is firmly reserved at the donor (UC01 steps 10–11, US10). Inventory writes
/// it to its outbox in the approval's transaction from stage 8, when Forecasting consumes it (#14): an integration event
/// is published only once at least one consumer exists; the contract is defined earlier (STAGE_5_PLAN section 4.1).
/// <see cref="RecommendationId"/> is null for transfers created by hand.
/// </summary>
public sealed record TransferApproved(
    long TransferOrderId,
    string Number,
    short FromWarehouseId,
    short ToWarehouseId,
    bool IsUrgent,
    long? RecommendationId,
    IReadOnlyList<TransferredPart> Lines,
    Guid ApprovedBy,
    DateTimeOffset ApprovedAt) : IntegrationEvent;

/// <summary>
/// A proposed or approved transfer order was cancelled; its firm reservations were released. Written to the outbox from
/// stage 8 with its first consumer (#14).
/// </summary>
public sealed record TransferCancelled(
    long TransferOrderId,
    string Number,
    long? RecommendationId,
    string Reason,
    DateTimeOffset CancelledAt) : IntegrationEvent;

/// <summary>
/// A requested transfer cannot be fulfilled: the donor's stock (SS200) or safety stock (SS201) does not allow it. Published
/// by the <c>TransferRequested</c> consumer, which is built in stage 8 together with its publisher (#14, STAGE_5_PLAN
/// section 4.1); Forecasting then returns the recommendation to open.
/// </summary>
public sealed record TransferRejected(
    long RecommendationId,
    string ErrorCode,
    string Message) : IntegrationEvent;

/// <summary>A catalogue part as other modules copy it (<c>fleet.part_snapshot</c>, <c>procurement.part_snapshot</c>).</summary>
public sealed record PartSnapshot(long PartId, string BrandCode, string OemPartNumber, string Description, string UnitOfMeasure, bool IsActive);

/// <summary>
/// Parts of the catalogue were added or changed (MODULE_MAPPING 2.3, 3.5, X22), in chunks of at most
/// <see cref="MaxParts"/> parts per event so a 20,000-part import stays within the broker budget (ADR 0017 §5).
/// Inventory writes it to its outbox from stage 6, when Fleet keeps its part snapshot (#12); the contract is defined
/// earlier (STAGE_5_PLAN section 4.1).
/// </summary>
public sealed record PartCatalogChanged(IReadOnlyList<PartSnapshot> Parts) : IntegrationEvent
{
    public const int MaxParts = 500;
}

/// <summary>The quantity Inventory holds for one service order line.</summary>
public sealed record ReservedKitLine(long LineId, decimal ReservedQty);

/// <summary>
/// The kit of a drafted service order was soft-reserved, partially if stock is short (STAGE_6_PLAN 5.3, 5.7; MODULE_MAPPING
/// X6). Fleet stores the per-line quantity as its <c>qty_reserved</c> copy and moves a fully reserved order to
/// <c>parts_reserved</c>. Published by Inventory's consumer of <c>ServiceOrderDrafted</c> from PR 6g2. Handled as
/// <see cref="ActorId"/>, the user who drafted the order.
/// </summary>
public sealed record ServiceKitReserved(
    long ServiceOrderId,
    IReadOnlyList<ReservedKitLine> Lines,
    bool FullyReserved,
    DateTimeOffset ExpiresAt,
    Guid ActorId) : IntegrationEvent, IActedByUser;
