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

/// <summary>A brand as the other modules copy it: id, code, name and whether it is archived.</summary>
public sealed record BrandSnapshot(short BrandId, string Code, string Name, bool IsArchived);

/// <summary>
/// Brands of Inventory's catalogue were added or changed (STAGE_7_PLAN 3.2, PR 7h1). Fleet and Identity keep read-only
/// copies (<c>fleet.brand_snapshot</c>, <c>iam.brand_snapshot</c>) with the same ids; the catalogue has a few dozen
/// brands, so one event carries every brand a write touched, and <c>inventory.brand-catalog-publish</c> sends them all
/// for the initial fill and as a repair tool.
/// </summary>
public sealed record BrandCatalogChanged(IReadOnlyList<BrandSnapshot> Brands) : IntegrationEvent;

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

/// <summary>
/// Soft holds of service kits expired after their 7 days (STAGE_6_PLAN 5.5, MODULE_MAPPING X5). Inventory's holds know the
/// service order line, not the order, so the event names the lines; Fleet sets their <c>qty_reserved</c> copy to 0 and
/// returns an order that was parts reserved to draft. Published by the expiry job from PR 6g3, in chunks of at most
/// <see cref="MaxLines"/> lines.
/// </summary>
public sealed record ServiceKitReservationExpired(IReadOnlyList<long> LineIds, DateTimeOffset ExpiredAt) : IntegrationEvent
{
    public const int MaxLines = 500;
}

/// <summary>What Inventory issued for one service order line, and what it could not.</summary>
public sealed record IssuedKitLine(long LineId, decimal IssuedQty, decimal ShortfallQty);

/// <summary>
/// The kit of a completed service order was issued (ISSUE_SERVICE movements, STAGE_6_PLAN 5.4, F7): reserved quantities
/// always, the rest up to the available stock; <see cref="IssuedKitLine.ShortfallQty"/> is what was missing. Fleet stores the
/// issued quantity per line. Published by Inventory's consumer of <c>ServiceOrderCompleted</c> from PR 6h2. Handled as
/// <see cref="ActorId"/>, the user who completed the order.
/// </summary>
public sealed record ServiceKitIssued(long ServiceOrderId, IReadOnlyList<IssuedKitLine> Lines, Guid ActorId) : IntegrationEvent, IActedByUser;

/// <summary>An employee in a rights-bearing assignment role of a record, e.g. KEEPER of a warehouse (ADR 0026).</summary>
public sealed record ResponsibleAssignment(Guid EmployeeId, string Role);

/// <summary>
/// The rights-bearing responsible employees of a brand or warehouse changed (ADR 0026 decision 3, STAGE_7_PLAN 1.3):
/// the full new set of that record (state transfer), so Identity replaces its projection and derives brand scope and hub
/// access from it. Idempotent and order-safe: Identity keeps the newer event per record. Written in the same transaction
/// as the change; <c>inventory.responsibility-publish</c> republishes every record.
/// </summary>
public sealed record ResponsibilitiesChanged(string RecordKind, short RecordId, IReadOnlyList<ResponsibleAssignment> Assignments, Guid ActorId)
    : IntegrationEvent, IActedByUser
{
    public const string Brand = "brand";

    public const string Warehouse = "warehouse";
}
