using NpgsqlTypes;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>PostgreSQL enum <c>procurement.po_status</c>.</summary>
internal enum PoStatus
{
    Draft,
    PendingApproval,
    Approved,
    Exported,
    PartiallyReceived,
    Received,
    Cancelled,
}

/// <summary>Row of <c>procurement.supplier</c>.</summary>
internal sealed class Supplier
{
    public int Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string CountryCode { get; set; } = null!;

    public string? OrderEmail { get; set; }

    public short DefaultLeadTimeDays { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>procurement.supplier_part</c>.</summary>
internal sealed class SupplierPart
{
    public long Id { get; set; }

    public int SupplierId { get; set; }

    public long PartId { get; set; }

    public string? SupplierPartNumber { get; set; }

    public decimal Moq { get; set; }

    public decimal PackSize { get; set; }

    public short LeadTimeDays { get; set; }

    public bool IsPreferred { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>procurement.supplier_price</c>.</summary>
internal sealed class SupplierPrice
{
    public long Id { get; set; }

    public long SupplierPartId { get; set; }

    public NpgsqlRange<DateOnly> ValidDuring { get; set; }

    public decimal UnitPrice { get; set; }
}

/// <summary>Row of <c>procurement.purchase_order</c>.</summary>
internal sealed class PurchaseOrder
{
    public long Id { get; set; }

    public string Number { get; set; } = null!;

    public int SupplierId { get; set; }

    public short WarehouseId { get; set; }

    public PoStatus Status { get; set; }

    public bool IsUrgent { get; set; }

    public string? UrgentReason { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public decimal TotalAmount { get; set; }

    public DateOnly? ExpectedArrivalDate { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? ExportedAt { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>procurement.purchase_order_line</c>.</summary>
internal sealed class PurchaseOrderLine
{
    public long Id { get; set; }

    public long PurchaseOrderId { get; set; }

    public long PartId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal? LineAmount { get; set; }

    public decimal ReceivedQty { get; set; }

    public decimal? ProposedQty { get; set; }

    public string? OverrideReason { get; set; }
}
