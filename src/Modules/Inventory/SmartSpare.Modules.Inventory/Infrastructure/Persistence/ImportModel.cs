namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// The raw and staging layers of the data import (DATABASE_DESIGN section 3): rows without behaviour, written by
// the import handler (raw) and the staging.* SQL functions (ADR 0013 rule 3).

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
