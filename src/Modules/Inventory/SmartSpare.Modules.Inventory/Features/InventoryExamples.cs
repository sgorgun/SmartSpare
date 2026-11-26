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
