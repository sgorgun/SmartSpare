using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain.Maintenance;

/// <summary>
/// An OEM maintenance plan of a machine model (aggregate root, read-mostly; ADR 0013): a service every
/// <see cref="IntervalHours"/> engine hours and, where the OEM also sets one, every <see cref="IntervalMonths"/>
/// months, whichever comes first (STAGE_6_PLAN F3). Its kit (<see cref="Parts"/>) is copied into each service order.
/// Plans are OEM reference data: maintained by migrations and the demo dataset, not through the API.
/// </summary>
internal sealed class MaintenancePlan : AggregateRoot<int>
{
    public const int MinIntervalHours = 10;
    public const int MaxIntervalHours = 20_000;
    public const short MinIntervalMonths = 1;
    public const short MaxIntervalMonths = 60;

    private readonly List<MaintenancePlanPart> _parts = [];

    private MaintenancePlan()
        : base(0)
    {
        Name = null!;
    }

    internal MaintenancePlan(int id, int modelId, string name, int intervalHours, short? intervalMonths = null,
        IEnumerable<MaintenancePlanPart>? parts = null, bool isActive = true)
        : base(id)
    {
        if (intervalHours is < MinIntervalHours or > MaxIntervalHours)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalHours));
        }

        if (intervalMonths is < MinIntervalMonths or > MaxIntervalMonths)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMonths));
        }

        ModelId = modelId;
        Name = name;
        IntervalHours = intervalHours;
        IntervalMonths = intervalMonths;
        IsActive = isActive;
        _parts.AddRange(parts ?? []);
    }

    public int ModelId { get; private set; }

    /// <summary>OEM interval in engine hours (e.g. 500).</summary>
    public int IntervalHours { get; private set; }

    /// <summary>OEM calendar interval in months, if the OEM sets one (e.g. 12).</summary>
    public short? IntervalMonths { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>The service kit: parts and quantities.</summary>
    public IReadOnlyList<MaintenancePlanPart> Parts => _parts.AsReadOnly();

    /// <summary>
    /// The interval under an operating condition: <c>round(interval_hours × factor, 1)</c>, exactly as
    /// <c>fleet.fn_maintenance_due</c> computes it (BA finding F5: severe dust, high load or non-OEM oil shorten it).
    /// </summary>
    public decimal EffectiveInterval(decimal conditionFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(conditionFactor);
        return decimal.Round(IntervalHours * conditionFactor, 1, MidpointRounding.AwayFromZero);
    }
}

/// <summary>One part of a maintenance plan's service kit (<c>fleet.maintenance_plan_part</c>).</summary>
internal sealed class MaintenancePlanPart
{
    private MaintenancePlanPart()
    {
    }

    internal MaintenancePlanPart(int maintenancePlanId, long partId, decimal quantity)
    {
        MaintenancePlanId = maintenancePlanId;
        PartId = partId;
        Quantity = quantity;
    }

    public int MaintenancePlanId { get; private set; }

    /// <summary>Plain reference to an Inventory part (no cross-module FK, MODULE_MAPPING 2.3).</summary>
    public long PartId { get; private set; }

    public decimal Quantity { get; private set; }
}
