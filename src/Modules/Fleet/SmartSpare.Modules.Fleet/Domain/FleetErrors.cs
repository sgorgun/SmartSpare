using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain;

/// <summary>
/// Expected business errors of the Fleet module. The domain returns them before the database is touched; the
/// triggers and SQL functions raise the same rules as SS100–SS300, mapped by <c>DatabaseErrorMapper</c> to the same codes.
/// </summary>
internal static class FleetErrors
{
    public static readonly Error InvalidEngineHours = Error.Validation(
        "Fleet.InvalidEngineHours", "Engine hours are between 0 and 100,000 with at most one decimal.");

    public static readonly Error InvalidQuantity = Error.Validation(
        "Fleet.InvalidQuantity", "A quantity is greater than zero and has at most three decimals.");

    public static readonly Error ReadingDecreased = Error.BusinessRule(
        "SS100", "Entered engine hours cannot be less than the previous recorded reading.");

    public static readonly Error ReadingImplausible = Error.BusinessRule(
        "SS101", "Implausible reading: the engine cannot run more hours than have passed since the last reading.");

    public static readonly Error MachineNotActive = Error.BusinessRule(
        "SS102", "The machine is not active.");

    public static readonly Error ReadingOutOfOrder = Error.BusinessRule(
        "SS103", "The reading time must be after the last reading.");

    /// <summary>
    /// SS104 (new in stage 6): the reading time lies in the future beyond the clock tolerance. The database guards the
    /// same rule with <c>ck_engine_hours_reading_not_future</c>, which <c>DatabaseErrorMapper</c> maps to SS104.
    /// </summary>
    public static readonly Error ReadingInFuture = Error.BusinessRule(
        "SS104", "The reading time cannot be in the future.");

    public static readonly Error ReadingConflict = Error.Conflict(
        "Fleet.ReadingConflict", "A reading with a different value already exists for this machine at this time.");

    public static readonly Error InvalidReadingSource = Error.Validation(
        "Fleet.InvalidReadingSource", "The reading source is manual, telematics or serviceVisit.");

    public static readonly Error NoLines = Error.Validation(
        "Fleet.NoLines", "A service order needs at least one part line.");

    public static readonly Error DuplicatePart = Error.Validation(
        "Fleet.DuplicatePart", "Each part appears only once in a service order.");

    public static readonly Error LineNotFound = Error.NotFound(
        "Fleet.LineNotFound", "The service order has no line for this part.");

    public static readonly Error CancelReasonRequired = Error.Validation(
        "Fleet.CancelReasonRequired", "A cancellation needs a reason of at most 300 characters.");

    public static readonly Error CompletionHoursTooLow = Error.Validation(
        "Fleet.CompletionHoursTooLow", "The engine hours at completion cannot be below the hours that triggered the order or the machine's current hours.");

    public static readonly Error MachineNotFound = Error.NotFound(
        "Fleet.MachineNotFound", "The machine does not exist.");

    public static readonly Error ServiceOrderNotFound = Error.NotFound(
        "Fleet.ServiceOrderNotFound", "The service order does not exist.");

    public static readonly Error MaintenancePlanNotFound = Error.NotFound(
        "Fleet.MaintenancePlanNotFound", "The maintenance plan does not exist or does not belong to the machine's model.");

    /// <summary>A state transition the state machine does not allow (SS300 in the database).</summary>
    public static Error InvalidTransition(object from, string action) => Error.Conflict(
        "SS300", $"A service order that is {Describe(from)} cannot be {action}.");

    private static string Describe(object status) => status switch
    {
        ServiceOrders.ServiceOrderStatus.PartsReserved => "parts reserved",
        ServiceOrders.ServiceOrderStatus.InProgress => "in progress",
        _ => status.ToString()!.ToLowerInvariant(),
    };
}
