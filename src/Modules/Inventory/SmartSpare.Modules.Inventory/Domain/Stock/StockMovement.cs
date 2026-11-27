using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>Codes of <c>ref.movement_type</c> used by the Inventory module, with their effect on the balance.</summary>
internal static class MovementTypes
{
    public const string OpeningBalance = "OPENING_BALANCE";
    public const string ReceiptPurchaseOrder = "RECEIPT_PO";
    public const string ReturnFromCustomer = "RETURN_FROM_CUSTOMER";
    public const string AdjustmentIn = "ADJUSTMENT_IN";
    public const string TransferIn = "TRANSFER_IN";
    public const string TransferOut = "TRANSFER_OUT";
    public const string IssueService = "ISSUE_SERVICE";
    public const string IssueSale = "ISSUE_SALE";
    public const string AdjustmentOut = "ADJUSTMENT_OUT";
    public const string HistoricalDemand = "HISTORICAL_DEMAND";

    // A lookup rather than a switch over ten strings: a Debug build compiles such a switch to a hash that calls the
    // compiler-generated <PrivateImplementationDetails> type, which the architecture test of the domain rejects.
    private static readonly Dictionary<string, int> BalanceSigns = new(StringComparer.Ordinal)
    {
        [OpeningBalance] = 1,
        [ReceiptPurchaseOrder] = 1,
        [ReturnFromCustomer] = 1,
        [AdjustmentIn] = 1,
        [TransferIn] = 1,
        [TransferOut] = -1,
        [IssueService] = -1,
        [IssueSale] = -1,
        [AdjustmentOut] = -1,
        [HistoricalDemand] = 0,
    };

    /// <summary>+1 adds to stock, -1 takes from it, 0 has no stock effect (the <c>balance_sign</c> column).</summary>
    public static int BalanceSign(string code) =>
        BalanceSigns.TryGetValue(code, out var sign)
            ? sign
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown movement type.");

    /// <summary>Whether the movement counts as demand for forecasting (the <c>is_demand</c> column).</summary>
    public static bool IsDemand(string code) => code is IssueService or IssueSale or HistoricalDemand;
}

/// <summary>
/// A row of the stock ledger (<c>inventory.stock_movement</c>, partitioned by month). Append-only: the trigger
/// <c>trg_stock_movement_immutable</c> refuses updates and deletes, and <c>trg_stock_movement_apply</c> applies the
/// quantity to the balance (SS200 if an issue exceeds the available stock). The application posts only stock
/// adjustments; every other movement type is posted by the SQL functions of its workflow.
/// </summary>
internal sealed class StockMovement : Entity<long>
{
    /// <summary>Length of <c>note</c>.</summary>
    public const int MaxNoteLength = 300;

    private StockMovement()
        : base(0)
    {
        MovementTypeCode = null!;
    }

    public DateTimeOffset OccurredAt { get; private set; }

    public short WarehouseId { get; private set; }

    public long PartId { get; private set; }

    public string MovementTypeCode { get; private set; }

    /// <summary>Always positive; the direction comes from the movement type.</summary>
    public decimal Quantity { get; private set; }

    public decimal? UnitCost { get; private set; }

    public long? ServiceOrderLineId { get; private set; }

    public long? TransferOrderLineId { get; private set; }

    public long? PurchaseOrderLineId { get; private set; }

    public long? ImportBatchId { get; private set; }

    public string? ExternalDocRef { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Why the stock was corrected; set on every adjustment since stage 7 (older ledger rows have none).</summary>
    public short? AdjustmentReasonId { get; private set; }

    public Guid? CreatedBy { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The signed effect on the on-hand stock.</summary>
    public decimal SignedQuantity => MovementTypes.BalanceSign(MovementTypeCode) * Quantity;

    /// <summary>
    /// A stock count correction: a positive <paramref name="signedQuantity"/> posts <c>ADJUSTMENT_IN</c>, a negative one
    /// <c>ADJUSTMENT_OUT</c>. The <paramref name="reason"/> must be active (SS500) and allow the direction; the note is
    /// required when the reason says so.
    /// </summary>
    public static Result<StockMovement> Adjust(
        short warehouseId, long partId, decimal signedQuantity, decimal unitCost, AdjustmentReason reason, string? note, Guid createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Domain.Quantity.Create(Math.Abs(signedQuantity)).IsFailure)
        {
            return InventoryErrors.InvalidQuantity;
        }

        if (reason.IsArchived)
        {
            return InventoryErrors.ReasonArchived;
        }

        if (!reason.Allows(signedQuantity))
        {
            return InventoryErrors.ReasonDirection;
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            return InventoryErrors.NoteTooLong;
        }

        if (trimmed is null && reason.RequiresNote)
        {
            return InventoryErrors.NoteRequired;
        }

        return new StockMovement
        {
            OccurredAt = now,
            WarehouseId = warehouseId,
            PartId = partId,
            MovementTypeCode = signedQuantity > 0 ? MovementTypes.AdjustmentIn : MovementTypes.AdjustmentOut,
            Quantity = Math.Abs(signedQuantity),
            UnitCost = unitCost,
            Note = trimmed,
            AdjustmentReasonId = reason.Id,
            CreatedBy = createdBy,
        };
    }
}
