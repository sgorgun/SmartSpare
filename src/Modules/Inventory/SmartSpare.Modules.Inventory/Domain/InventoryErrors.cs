using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain;

/// <summary>
/// Expected business errors of the Inventory module. The domain returns them before the database is touched; the
/// SQL functions and triggers raise the same rules as SS200–SS302, mapped by <c>DatabaseErrorMapper</c>.
/// </summary>
internal static class InventoryErrors
{
    public static readonly Error InvalidQuantity = Error.Validation(
        "Inventory.InvalidQuantity", "A quantity is greater than zero and has at most three decimals.");

    public static readonly Error SameWarehouse = Error.Validation(
        "Inventory.SameWarehouse", "A transfer needs two different warehouses.");

    public static readonly Error NoLines = Error.Validation(
        "Inventory.NoLines", "A transfer needs at least one line.");

    public static readonly Error DuplicatePart = Error.Validation(
        "Inventory.DuplicatePart", "Each part appears only once in a transfer.");

    public static readonly Error OverrideReasonRequired = Error.Validation(
        "Inventory.OverrideReasonRequired", "A quantity that differs from the proposal needs a reason.");

    public static readonly Error CancelReasonRequired = Error.Validation(
        "Inventory.CancelReasonRequired", "A cancellation needs a reason of at most 300 characters.");

    public static readonly Error NoteRequired = Error.Validation(
        "Inventory.NoteRequired", "A stock adjustment needs a note of at most 300 characters.");

    public static readonly Error InvalidStockLevels = Error.Validation(
        "Inventory.InvalidStockLevels", "The safety stock is zero or more, and the maximum is not below it.");

    public static readonly Error LineNotFound = Error.NotFound(
        "Inventory.LineNotFound", "The transfer has no line for this part.");

    public static readonly Error TransferNotFound = Error.NotFound(
        "Inventory.TransferNotFound", "The transfer order does not exist.");

    public static readonly Error StockPositionNotFound = Error.NotFound(
        "Inventory.StockPositionNotFound", "The part is not stocked in this warehouse.");

    public static readonly Error NotYourWarehouse = Error.Forbidden(
        "SS302", "Only a keeper of the warehouse concerned can do this.");

    public static readonly Error InsufficientStock = Error.Conflict(
        "SS200", "There is not enough available stock.");

    public static readonly Error BelowSafetyStock = Error.Conflict(
        "SS201", "The donor warehouse would fall below its safety stock.");

    /// <summary>A state transition the state machine does not allow (SS300 in the database).</summary>
    public static Error InvalidTransition(string document, object from, string action) => Error.Conflict(
        "SS300", $"A {document} that is {from.ToString()!.ToLowerInvariant()} cannot be {action}.");
}
