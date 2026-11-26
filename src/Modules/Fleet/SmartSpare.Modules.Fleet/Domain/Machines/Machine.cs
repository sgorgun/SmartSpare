using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain.Machines;

/// <summary>PostgreSQL enum <c>fleet.machine_status</c>.</summary>
internal enum MachineStatus
{
    Active,
    Inactive,
    Decommissioned,
}

/// <summary>
/// A customer machine of the installed base (aggregate root, ADR 0013). Its odometer (<see cref="CurrentEngineHours"/>,
/// <see cref="EngineHoursReadAt"/>) is written only by the trigger <c>fleet.trg_engine_hours_apply</c> when a reading is
/// inserted through <c>fleet.log_engine_hours</c>; <see cref="CheckReading"/> applies the same rules as
/// <c>fleet.trg_engine_hours_validate</c> first, so the user gets a readable answer before the database is touched
/// (STAGE_6_PLAN section 3.1).
/// </summary>
internal sealed class Machine : AggregateRoot<long>
{
    /// <summary>Clock tolerance of <c>ck_engine_hours_reading_not_future</c>.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private Machine()
        : base(0)
    {
        Vin = null!;
    }

    internal Machine(long id, string vin, int modelId, long customerId, short operatingConditionId, short homeWarehouseId,
        short manufactureYear, MachineStatus status = MachineStatus.Active, DateOnly? commissionedOn = null)
        : base(id)
    {
        Vin = vin;
        ModelId = modelId;
        CustomerId = customerId;
        OperatingConditionId = operatingConditionId;
        HomeWarehouseId = homeWarehouseId;
        ManufactureYear = manufactureYear;
        Status = status;
        CommissionedOn = commissionedOn;
    }

    public string Vin { get; private set; }

    public int ModelId { get; private set; }

    public long CustomerId { get; private set; }

    public short OperatingConditionId { get; private set; }

    /// <summary>The hub where the machine's service kits are reserved.</summary>
    public short HomeWarehouseId { get; private set; }

    public short ManufactureYear { get; private set; }

    /// <summary>Latest odometer value; maintained by the reading trigger.</summary>
    public decimal CurrentEngineHours { get; private set; }

    /// <summary>Time of the latest reading; maintained by the reading trigger.</summary>
    public DateTimeOffset? EngineHoursReadAt { get; private set; }

    public MachineStatus Status { get; private set; }

    public DateOnly? CommissionedOn { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Maintained by the <c>updated_at</c> trigger.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == MachineStatus.Active;

    /// <summary>
    /// UC02 step 3 and A1: may a reading of <paramref name="hours"/> taken at <paramref name="readAt"/> follow
    /// <paramref name="lastReading"/>? Mirrors <c>fleet.trg_engine_hours_validate</c> (SS100–SS103) and
    /// <c>ck_engine_hours_reading_not_future</c>.
    /// </summary>
    public Result CheckReading(decimal hours, DateTimeOffset readAt, EngineHoursReading? lastReading, DateTimeOffset now)
    {
        var value = EngineHours.Create(hours);
        if (value.IsFailure)
        {
            return value.Error;
        }

        if (!IsActive)
        {
            return FleetErrors.MachineNotActive;
        }

        if (readAt > now + FutureTolerance)
        {
            return FleetErrors.ReadingInFuture;
        }

        if (lastReading is null)
        {
            return Result.Success();
        }

        if (readAt <= lastReading.ReadAt)
        {
            return FleetErrors.ReadingOutOfOrder;
        }

        if (hours < lastReading.ReadingHours)
        {
            return FleetErrors.ReadingDecreased;
        }

        // An engine cannot run more hours than wall-clock time has passed (at most 24 h per day).
        var elapsedHours = (decimal)(readAt - lastReading.ReadAt).TotalHours;
        return hours - lastReading.ReadingHours > elapsedHours
            ? FleetErrors.ReadingImplausible
            : Result.Success();
    }
}
