using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features;

/// <summary>Example bodies shared by the module's endpoint descriptions (ADR 0019 §1).</summary>
internal static class InventoryExamples
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 30, 0, TimeSpan.Zero);

    public static IReadOnlyDictionary<string, object?> Unauthorized { get; } =
        OpenApiConventions.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", ProblemCodes.Unauthenticated);

    public static StockRow StockRow { get; } = new(
        WarehouseId: 3, WarehouseCode: "KLP", PartId: 1042, BrandCode: "JD", OemPartNumber: "RE900001", Description: "Engine oil filter",
        UnitOfMeasure: "PCS", OnHand: 24, Reserved: 13, Available: 11, SafetyStock: 6, MaxStock: 30, InTransit: 4,
        Status: StockStatuses.Ok, LastMovementAt: At);

    public static MovementRow Movement { get; } = new(
        Id: 884210, OccurredAt: At, WarehouseId: 3, WarehouseCode: "KLP", PartId: 1042, OemPartNumber: "RE900001",
        MovementType: "TRANSFER_OUT", Quantity: -11, Document: "TR-2026-000412", Note: null);

    public static StockPositionDetails StockPosition { get; } = new(
        StockRow,
        BinLocation: "A-12-03",
        Version: "7731",
        Reservations: [new ReservationSummary(5521, 11, "firm", null, 412, "TR-2026-000412", null, At.AddHours(-2))],
        RecentMovements: [Movement]);

    public static TransferSummary Transfer { get; } = new(
        Id: 412, Number: "TR-2026-000412", FromWarehouseId: 3, FromWarehouseCode: "KLP", ToWarehouseId: 5, ToWarehouseCode: "PNV",
        Status: TransferStatuses.Proposed, IsUrgent: false, LineCount: 1, TotalQuantity: 11, CreatedAt: At.AddHours(-1), UpdatedAt: At.AddHours(-1));

    public static TransferDetails TransferDetails { get; } = new(
        Transfer,
        Lines: [new TransferLineResponse(907, 1042, "JD", "RE900001", "Engine oil filter", "PCS", 11, ProposedQty: 11, OverrideReason: null, DonorAvailable: 24)],
        Timeline: [new TransferStep("requested", Guid.Parse("6f1c2a9e-3b7d-4c11-9a52-0d8e4b7f3a21"), At.AddHours(-1))],
        CancelReason: null,
        AllowedActions: [TransferActions.Approve, TransferActions.Cancel]);

    public static ImportBatchDetails ImportDetails { get; } = new(
        new ImportBatchResponse(
            Id: 58, Source: ImportSources.PartCatalog, FileName: "jd-catalogue-2026-11.csv", Status: ImportStatuses.Loaded, TotalRows: 1200,
            ValidRows: 1196, ErrorRows: 4, LoadedRows: 1196, UploadedBy: Guid.Parse("6f1c2a9e-3b7d-4c11-9a52-0d8e4b7f3a21"),
            UploadedAt: At, ProcessedAt: At.AddSeconds(3), Message: "1200 rows: 1196 loaded/updated, 4 invalid, 0 duplicates skipped"),
        Errors: [new ImportRowError(17, "unit_cost", "TYPE", "Invalid unit cost \"12,5O\"")],
        ErrorCount: 4);

    /// <summary>The problem body an expected error produces, as <c>ResultHttpExtensions.ToProblem</c> writes it.</summary>
    public static IReadOnlyDictionary<string, object?> Problem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = ResultHttpExtensions.StatusCodeFor(error.Type);
        var title = error.Type switch
        {
            ErrorType.Validation => "Validation failed",
            ErrorType.NotFound => "Resource not found",
            ErrorType.Conflict => "Conflict",
            ErrorType.Forbidden => "Operation not permitted",
            ErrorType.BusinessRule => "Business rule violated",
            _ => "Error",
        };
        return OpenApiConventions.Problem(status, title, error.Code, error.Description);
    }

    /// <summary>A 400 validation problem with one failed field, as <c>ResultHttpExtensions.ToProblem</c> writes it.</summary>
    public static IReadOnlyDictionary<string, object?> ValidationProblem(string field, string message) =>
        new Dictionary<string, object?>(
            OpenApiConventions.Problem(StatusCodes.Status400BadRequest, "Validation failed", "Validation", "One or more validation errors occurred."),
            StringComparer.Ordinal)
        {
            ["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] },
        };
}
