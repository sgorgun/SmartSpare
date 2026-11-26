using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain;

/// <summary>
/// An odometer value in engine hours: 0–100,000 with at most one decimal (<c>numeric(8,1)</c> and the CHECKs
/// <c>ck_engine_hours_reading_hours</c> / <c>ck_machine_engine_hours</c>). Validates input before the database would.
/// </summary>
internal readonly record struct EngineHours
{
    public const decimal Max = 100_000m;

    private EngineHours(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<EngineHours> Create(decimal value) =>
        value is < 0 or > Max || decimal.Round(value, 1) != value
            ? FleetErrors.InvalidEngineHours
            : new EngineHours(value);

    public override string ToString() => Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
