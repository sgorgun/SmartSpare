using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Modules.Fleet.Features;

/// <summary>Example bodies shared by the Fleet endpoint descriptions (ADR 0019 §1), from the UC02 demo machine.</summary>
internal static class FleetExamples
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 30, 0, TimeSpan.Zero);

    public static IReadOnlyDictionary<string, object?> Unauthorized { get; } =
        OpenApiConventions.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", ProblemCodes.Unauthenticated);

    public static IReadOnlyDictionary<string, object?> Forbidden { get; } =
        OpenApiConventions.Problem(StatusCodes.Status403Forbidden, "Forbidden", ProblemCodes.Forbidden);

    public static MaintenancePlanDue EngineService { get; } = new(
        MaintenancePlanId: 21, PlanName: "Engine service", IntervalHours: 500, IntervalMonths: 12, EffectiveInterval: 500,
        DueEngineHours: 1000, RemainingHours: 488, ProjectedDueDate: new DateOnly(2027, 2, 14), CalendarDueDate: new DateOnly(2026, 6, 15),
        DueDate: new DateOnly(2026, 6, 15), Trigger: MaintenanceDueTriggers.Calendar, Status: MaintenanceDueStatuses.Planned,
        OpenOrderThreshold: 500);

    public static MaintenancePlanDue TransmissionService { get; } = new(
        MaintenancePlanId: 22, PlanName: "Transmission & hydraulic service", IntervalHours: 1500, IntervalMonths: 24, EffectiveInterval: 1500,
        DueEngineHours: 1500, RemainingHours: 988, ProjectedDueDate: new DateOnly(2027, 9, 30), CalendarDueDate: new DateOnly(2027, 6, 15),
        DueDate: new DateOnly(2027, 6, 15), Trigger: MaintenanceDueTriggers.Calendar, Status: MaintenanceDueStatuses.Ok,
        OpenOrderThreshold: null);

    public static MachineSummary Machine { get; } = new(
        Id: 501, Vin: "LX901PNV2026", ModelName: "Lexion 8700", BrandCode: "CLAAS", MachineType: "COMBINE", CustomerCode: "CUST-000007",
        WarehouseId: 5, WarehouseCode: "PNV", EngineHours: 512, LastReadingAt: At, MachineStatus: "active", NextService: EngineService);

    public static ReadingRow Reading { get; } = new(
        Id: 2004, ReadAt: At, Hours: 512, Source: ReadingSources.Manual, RecordedBy: Guid.Parse("00000000-0000-4000-8000-000000000004"));

    public static MachineDetails MachineDetails { get; } = new(
        Machine, ManufactureYear: 2025, CommissionedOn: new DateOnly(2025, 6, 15), OperatingCondition: "NORMAL", IntervalFactor: 1.0m,
        UsagePerDay: 3.6m, Plans: [EngineService, TransmissionService],
        RecentReadings: [Reading, new ReadingRow(1998, At.AddDays(-20), 440, ReadingSources.ServiceVisit, null)]);

    public static DueMaintenanceRow DueRow { get; } = new(
        MachineId: 77, Vin: "3F2A9C1B7D4E8F012", ModelName: "8R 410", BrandCode: "JD", CustomerCode: "CUST-000031", WarehouseId: 3,
        WarehouseCode: "KLP", EngineHours: 1478.5m,
        Due: TransmissionService with { MaintenancePlanId = 3, DueEngineHours = 1500, RemainingHours = 21.5m, ProjectedDueDate = new DateOnly(2026, 12, 2), DueDate = new DateOnly(2026, 12, 2), Trigger = MaintenanceDueTriggers.EngineHours, Status = MaintenanceDueStatuses.DueSoon });

    public static MaintenancePlanResponse Plan { get; } = new(
        Id: 21, ModelId: 7, ModelName: "Lexion 8700", BrandCode: "CLAAS", Name: "Engine service", IntervalHours: 500, IntervalMonths: 12,
        Kit: [new KitPart(1203, "0011534571", "Engine oil filter", "PCS", 1), new KitPart(1210, "0001970050", "Engine oil 15W-40", "L", 30)]);

    /// <summary>UC02: 512 h logged on the Lexion crosses the 500 h engine service, which is drafted with its kit.</summary>
    public static RecordReadingResponse RecordedReading { get; } = new(
        Reading, EngineHours: 512, Replayed: false, DraftedOrders: [new DraftedServiceOrder(9001, "SO-2026-000412", 500, FullyReserved: true)]);
}
