using System.ComponentModel;

namespace SmartSpare.ApiContracts.Fleet;

/// <summary>Due status of a maintenance plan on a machine (STAGE_6_PLAN section 2.5; values of <c>status</c> fields).</summary>
public static class MaintenanceDueStatuses
{
    /// <summary>The engine-hour threshold or the calendar date has passed.</summary>
    public const string Overdue = "overdue";

    /// <summary>Within 50 engine hours, or the due date is within the planning horizon.</summary>
    public const string DueSoon = "dueSoon";

    /// <summary>An open service order of the plan exists.</summary>
    public const string Planned = "planned";

    /// <summary>Not due within the horizons.</summary>
    public const string Ok = "ok";

    /// <summary>From the most to the least urgent.</summary>
    public static IReadOnlyList<string> All { get; } = [Overdue, DueSoon, Planned, Ok];
}

/// <summary>Which interval makes a plan due first (values of <c>trigger</c> fields).</summary>
public static class MaintenanceDueTriggers
{
    public const string EngineHours = "engineHours";

    public const string Calendar = "calendar";
}

/// <summary>Where an engine-hour reading came from (values of <c>source</c> fields).</summary>
public static class ReadingSources
{
    public const string Manual = "manual";

    public const string Telematics = "telematics";

    public const string ServiceVisit = "serviceVisit";

    public const string Import = "import";

    public static IReadOnlyList<string> All { get; } = [Manual, Telematics, ServiceVisit, Import];
}

/// <summary>Limits of the Fleet lists.</summary>
public static class FleetSearch
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxSearchLength = 40;

    /// <summary>Default planning horizon of the due list in days.</summary>
    public const int DefaultHorizonDays = 14;

    public const int MaxHorizonDays = 365;
}

/// <summary>The due calculation of one maintenance plan on one machine (MAINTENANCE_PLANNING.md).</summary>
[Description("The due calculation of one maintenance plan on one machine.")]
public sealed record MaintenancePlanDue(
    [property: Description("Maintenance plan id.")] int MaintenancePlanId,
    [property: Description("Plan name, for example Engine service.")] string PlanName,
    [property: Description("OEM interval in engine hours.")] int IntervalHours,
    [property: Description("OEM calendar interval in months, if the OEM sets one.")] short? IntervalMonths,
    [property: Description("Interval under the machine's operating condition: round(interval × factor, 1).")] decimal EffectiveInterval,
    [property: Description("Next engine-hour threshold D.")] decimal DueEngineHours,
    [property: Description("D minus the current engine hours; zero or negative when overdue.")] decimal RemainingHours,
    [property: Description("Day the machine reaches D at its average usage; null if the usage is unknown.")] DateOnly? ProjectedDueDate,
    [property: Description("Last service (or commissioning) plus the calendar interval; null without a calendar interval.")] DateOnly? CalendarDueDate,
    [property: Description("The earlier of the projected and the calendar date.")] DateOnly? DueDate,
    [property: Description("Which interval gives the due date: engineHours or calendar.")] string Trigger,
    [property: Description("One of MaintenanceDueStatuses.")] string Status,
    [property: Description("Threshold of the open service order of the plan, if any.")] decimal? OpenOrderThreshold);

/// <summary>One machine in the machine list.</summary>
[Description("One machine in the machine list.")]
public sealed record MachineSummary(
    [property: Description("Machine id.")] long Id,
    [property: Description("Vehicle / product identification number.")] string Vin,
    [property: Description("Model name.")] string ModelName,
    [property: Description("Brand code.")] string BrandCode,
    [property: Description("Machine type code, for example COMBINE.")] string MachineType,
    [property: Description("Pseudonymous customer code.")] string CustomerCode,
    [property: Description("Home hub id, where service kits are reserved.")] short WarehouseId,
    [property: Description("Home hub code.")] string WarehouseCode,
    [property: Description("Current engine hours.")] decimal EngineHours,
    [property: Description("Time of the latest reading.")] DateTimeOffset? LastReadingAt,
    [property: Description("Machine status: active, inactive or decommissioned.")] string MachineStatus,
    [property: Description("The most urgent plan of the machine, if it has plans.")] MaintenancePlanDue? NextService);

/// <summary>One engine-hour reading.</summary>
[Description("One engine-hour reading.")]
public sealed record ReadingRow(
    [property: Description("Reading id.")] long Id,
    [property: Description("Time the reading was taken.")] DateTimeOffset ReadAt,
    [property: Description("Engine hours.")] decimal Hours,
    [property: Description("One of ReadingSources.")] string Source,
    [property: Description("User who recorded it; null for imports.")] Guid? RecordedBy);

/// <summary>The machine card (WF03).</summary>
[Description("The machine card: data, engine hours, usage and every plan with its due calculation.")]
public sealed record MachineDetails(
    [property: Description("The machine.")] MachineSummary Machine,
    [property: Description("Year of manufacture.")] short ManufactureYear,
    [property: Description("Commissioning date, if known.")] DateOnly? CommissionedOn,
    [property: Description("Operating condition code, for example SEVERE_DUST.")] string OperatingCondition,
    [property: Description("Interval factor of the operating condition.")] decimal IntervalFactor,
    [property: Description("Average engine hours per day; null if the reading history is too short.")] decimal? UsagePerDay,
    [property: Description("Every active plan of the machine's model with its due calculation, most urgent first.")] IReadOnlyList<MaintenancePlanDue> Plans,
    [property: Description("The latest readings, newest first (at most 5).")] IReadOnlyList<ReadingRow> RecentReadings);

/// <summary>One plan of one machine in the due-maintenance list.</summary>
[Description("One plan of one machine in the due-maintenance list.")]
public sealed record DueMaintenanceRow(
    [property: Description("Machine id.")] long MachineId,
    [property: Description("Vehicle / product identification number.")] string Vin,
    [property: Description("Model name.")] string ModelName,
    [property: Description("Brand code.")] string BrandCode,
    [property: Description("Pseudonymous customer code.")] string CustomerCode,
    [property: Description("Home hub id.")] short WarehouseId,
    [property: Description("Home hub code.")] string WarehouseCode,
    [property: Description("Current engine hours.")] decimal EngineHours,
    [property: Description("The due calculation.")] MaintenancePlanDue Due,
    [property: Description("True when the home hub is archived in Inventory: no service order can be drafted until the machine moves to an active hub (ADR 0029).")] bool HubArchived = false);

/// <summary>One part of a service kit.</summary>
[Description("One part of a service kit, with the data of Fleet's catalogue copy.")]
public sealed record KitPart(
    [property: Description("Part id.")] long PartId,
    [property: Description("Manufacturer part number; null if the part is not in Fleet's catalogue copy yet.")] string? OemPartNumber,
    [property: Description("Part description; null if unknown.")] string? Description,
    [property: Description("Unit of measure code; null if unknown.")] string? UnitOfMeasure,
    [property: Description("Quantity in the kit.")] decimal Quantity);

/// <summary>An OEM maintenance plan with its kit.</summary>
[Description("An OEM maintenance plan of a machine model with its service kit.")]
public sealed record MaintenancePlanResponse(
    [property: Description("Plan id.")] int Id,
    [property: Description("Machine model id.")] int ModelId,
    [property: Description("Model name.")] string ModelName,
    [property: Description("Brand code.")] string BrandCode,
    [property: Description("Plan name.")] string Name,
    [property: Description("OEM interval in engine hours.")] int IntervalHours,
    [property: Description("OEM calendar interval in months, if any.")] short? IntervalMonths,
    [property: Description("The service kit.")] IReadOnlyList<KitPart> Kit);
