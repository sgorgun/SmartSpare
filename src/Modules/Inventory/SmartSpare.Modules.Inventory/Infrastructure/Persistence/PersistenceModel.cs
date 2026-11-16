namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>PostgreSQL enum <c>inventory.reservation_status</c>.</summary>
internal enum ReservationStatus
{
    Soft,
    Firm,
    Consumed,
    Released,
    Expired,
}

/// <summary>PostgreSQL enum <c>inventory.transfer_status</c>.</summary>
internal enum TransferStatus
{
    Proposed,
    Approved,
    Dispatched,
    Received,
    Cancelled,
}

/// <summary>PostgreSQL enum <c>raw.import_source</c>.</summary>
internal enum ImportSource
{
    PartCatalog,
    DemandHistory,
}

/// <summary>PostgreSQL enum <c>raw.import_status</c>.</summary>
internal enum ImportStatus
{
    Uploaded,
    Validated,
    Loaded,
    Failed,
}

/// <summary>Row of <c>catalog.part</c>.</summary>
internal sealed class Part
{
    public long Id { get; set; }

    public short BrandId { get; set; }

    public string OemPartNumber { get; set; } = null!;

    public string Description { get; set; } = null!;

    public int CategoryId { get; set; }

    public short UomId { get; set; }

    public decimal? WeightKg { get; set; }

    public decimal UnitCost { get; set; }

    public bool IsConditionBased { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>catalog.part_analogue</c>.</summary>
internal sealed class PartAnalogue
{
    public long PartId { get; set; }

    public long AnaloguePartId { get; set; }

    public decimal ScalingFactor { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>inventory.demand_exception</c>.</summary>
internal sealed class DemandException
{
    public long MovementId { get; set; }

    public DateTimeOffset MovementOccurredAt { get; set; }

    public string Reason { get; set; } = null!;

    public string DetectionMethod { get; set; } = null!;

    public Guid? FlaggedBy { get; set; }

    public DateTimeOffset FlaggedAt { get; set; }
}

/// <summary>Row of <c>inventory.stock_balance</c>.</summary>
internal sealed class StockBalance
{
    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public decimal OnHandQty { get; set; }

    public decimal ReservedQty { get; set; }

    public decimal? AvailableQty { get; set; }

    public DateTimeOffset? LastMovementAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>inventory.stock_movement</c>.</summary>
internal sealed class StockMovement
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public string MovementTypeCode { get; set; } = null!;

    public decimal Quantity { get; set; }

    public decimal? UnitCost { get; set; }

    public long? ServiceOrderLineId { get; set; }

    public long? TransferOrderLineId { get; set; }

    public long? PurchaseOrderLineId { get; set; }

    public long? ImportBatchId { get; set; }

    public string? ExternalDocRef { get; set; }

    public string? Note { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>inventory.stock_reservation</c>.</summary>
internal sealed class StockReservation
{
    public long Id { get; set; }

    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public decimal Quantity { get; set; }

    public ReservationStatus Status { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public long? ServiceOrderLineId { get; set; }

    public long? TransferOrderLineId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>Row of <c>inventory.transfer_order</c>.</summary>
internal sealed class TransferOrder
{
    public long Id { get; set; }

    public string Number { get; set; } = null!;

    public short FromWarehouseId { get; set; }

    public short ToWarehouseId { get; set; }

    public TransferStatus Status { get; set; }

    public bool IsUrgent { get; set; }

    public Guid? RequestedBy { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? DispatchedBy { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public Guid? ReceivedBy { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>inventory.transfer_order_line</c>.</summary>
internal sealed class TransferOrderLine
{
    public long Id { get; set; }

    public long TransferOrderId { get; set; }

    public long PartId { get; set; }

    public decimal Quantity { get; set; }

    public decimal? ProposedQty { get; set; }

    public string? OverrideReason { get; set; }
}

/// <summary>Row of <c>inventory.warehouse</c>.</summary>
internal sealed class Warehouse
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string City { get; set; } = null!;

    public string? Address { get; set; }

    public bool IsCentralHub { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Row of <c>inventory.warehouse_part</c>.</summary>
internal sealed class WarehousePart
{
    public short WarehouseId { get; set; }

    public long PartId { get; set; }

    public string? BinLocation { get; set; }

    public decimal SafetyStockQty { get; set; }

    public decimal? MaxStockQty { get; set; }

    public bool IsStocked { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>inventory.warehouse_route</c>.</summary>
internal sealed class WarehouseRoute
{
    public short FromWarehouseId { get; set; }

    public short ToWarehouseId { get; set; }

    public decimal DistanceKm { get; set; }

    public decimal TransitHours { get; set; }

    public decimal TransportCostEur { get; set; }
}

/// <summary>Row of <c>raw.demand_history_row</c>.</summary>
internal sealed class DemandHistoryRow
{
    public long ImportBatchId { get; set; }

    public int LineNo { get; set; }

    public string? DocDate { get; set; }

    public string? WarehouseCode { get; set; }

    public string? BrandCode { get; set; }

    public string? OemPartNumber { get; set; }

    public string? Quantity { get; set; }

    public string? DocRef { get; set; }
}

/// <summary>Row of <c>raw.import_batch</c>.</summary>
internal sealed class ImportBatch
{
    public long Id { get; set; }

    public ImportSource Source { get; set; }

    public string FileName { get; set; } = null!;

    public string FileSha256 { get; set; } = null!;

    public ImportStatus Status { get; set; }

    public int TotalRows { get; set; }

    public int? ValidRows { get; set; }

    public int? ErrorRows { get; set; }

    public int? LoadedRows { get; set; }

    public Guid? UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Message { get; set; }
}

/// <summary>Row of <c>raw.part_catalog_row</c>.</summary>
internal sealed class PartCatalogRow
{
    public long ImportBatchId { get; set; }

    public int LineNo { get; set; }

    public string? BrandCode { get; set; }

    public string? OemPartNumber { get; set; }

    public string? Description { get; set; }

    public string? CategoryCode { get; set; }

    public string? UomCode { get; set; }

    public string? WeightKg { get; set; }

    public string? UnitCost { get; set; }

    public string? ConditionBased { get; set; }
}

/// <summary>Row of <c>staging.demand_history</c>.</summary>
internal sealed class DemandHistory
{
    public long ImportBatchId { get; set; }

    public int LineNo { get; set; }

    public DateTimeOffset? OccurredAt { get; set; }

    public short? WarehouseId { get; set; }

    public long? PartId { get; set; }

    public decimal? Quantity { get; set; }

    public string? DocRef { get; set; }

    public byte[]? RowHash { get; set; }

    public bool IsValid { get; set; }

    public bool IsDuplicate { get; set; }
}

/// <summary>Row of <c>staging.import_error</c>.</summary>
internal sealed class ImportError
{
    public long Id { get; set; }

    public long ImportBatchId { get; set; }

    public int LineNo { get; set; }

    public string ColumnName { get; set; } = null!;

    public string ErrorCode { get; set; } = null!;

    public string Message { get; set; } = null!;
}

/// <summary>Row of <c>staging.part_catalog</c>.</summary>
internal sealed class PartCatalog
{
    public long ImportBatchId { get; set; }

    public int LineNo { get; set; }

    public short? BrandId { get; set; }

    public string? OemPartNumber { get; set; }

    public string? Description { get; set; }

    public int? CategoryId { get; set; }

    public short? UomId { get; set; }

    public decimal? WeightKg { get; set; }

    public decimal? UnitCost { get; set; }

    public bool? IsConditionBased { get; set; }

    public bool IsValid { get; set; }
}

/// <summary>Platform reference table <c>ref.brand</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefBrand
{
    public short Id { get; set; }
}

/// <summary>Platform reference table <c>ref.part_category</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefPartCategory
{
    public int Id { get; set; }
}

/// <summary>Platform reference table <c>ref.unit_of_measure</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefUnitOfMeasure
{
    public short Id { get; set; }
}
