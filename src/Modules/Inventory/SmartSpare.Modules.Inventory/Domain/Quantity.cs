using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain;

/// <summary>
/// A positive quantity of a part in its unit of measure, as stored in <c>numeric(12,3)</c> columns: greater than
/// zero, at most three decimals and below 10^9. Validates commands before the CHECK constraints would.
/// </summary>
internal sealed class Quantity : ValueObject
{
    /// <summary>Exclusive upper bound of <c>numeric(12,3)</c>.</summary>
    public const decimal Limit = 1_000_000_000m;

    private Quantity(decimal value) => Value = value;

    public decimal Value { get; }

    public static Result<Quantity> Create(decimal value) =>
        value > 0 && value < Limit && decimal.Round(value, 3) == value
            ? new Quantity(value)
            : InventoryErrors.InvalidQuantity;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        // decimal equality and hashing ignore the scale: 5 and 5.000 are the same quantity.
        yield return Value;
    }
}
