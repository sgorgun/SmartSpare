using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory.Domain.Catalog;

/// <summary>
/// A spare part (SKU) of <c>catalog.part</c> on the shared catalogue mechanism (STAGE_7_PLAN 3.1, PR 7i1). Its code is
/// the OEM part number, unique per brand, and its name is the description (up to 300 characters). The brand is part of
/// the key and never changes; an archived brand or manufacturer cannot be chosen (SS500, also refused by the database),
/// and a part with stock, open transfers, kit holds or responsibles cannot be archived (SS501).
/// </summary>
internal sealed partial class Part : MasterRecord<long>
{
    public const int MaxDescriptionLength = 300;

    public const int MinDescriptionLength = 3;

    public static readonly Error InvalidOemNumber = Error.Validation(
        "Inventory.InvalidOemNumber", "An OEM part number has 3 to 40 capital letters, digits, '.', '-' or '/', starting with a letter or digit.");

    public static readonly Error InvalidDescription = Error.Validation(
        "Inventory.InvalidPartDescription", $"The description has {MinDescriptionLength} to {MaxDescriptionLength} characters.");

    public static readonly Error InvalidCost = Error.Validation("Inventory.InvalidUnitCost", "The unit cost is zero or more.");

    public static readonly Error InvalidWeight = Error.Validation("Inventory.InvalidWeight", "The weight is zero or more, or empty.");

    private Part()
    {
    }

    private Part(short brandId, string oemPartNumber, string description)
        : base(oemPartNumber, description)
    {
        BrandId = brandId;
    }

    /// <summary>The brand (catalog.brand); with the OEM number the natural key, so it never changes.</summary>
    public short BrandId { get; private set; }

    public int CategoryId { get; private set; }

    public short UomId { get; private set; }

    public decimal? WeightKg { get; private set; }

    /// <summary>Standard (valuation) cost per unit, EUR.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Replaced "as required" (air and cab filters): demand only from the forecast stream.</summary>
    public bool IsConditionBased { get; private set; }

    /// <summary>The maker (catalog.manufacturer), if known; clearing it is done here, never by the import.</summary>
    public short? ManufacturerId { get; private set; }

    public Brand? Brand { get; private set; }

    public Manufacturer? Manufacturer { get; private set; }

    public PartCategory? Category { get; private set; }

    public UnitOfMeasure? Unit { get; private set; }

    /// <summary>The OEM number as stored: upper case, no spaces.</summary>
    public static Result<string> NormaliseCode(string? code)
    {
        var normalised = WhiteSpace().Replace(code?.Trim() ?? string.Empty, string.Empty).ToUpperInvariant();
        return OemPattern().IsMatch(normalised) ? normalised : InvalidOemNumber;
    }

    public static Result<Part> Create(
        short brandId, string? oemPartNumber, string? description, int categoryId, short uomId, decimal? weightKg, decimal unitCost,
        bool isConditionBased, short? manufacturerId)
    {
        var code = NormaliseCode(oemPartNumber);
        if (code.IsFailure)
        {
            return code.Error;
        }

        var part = new Part(brandId, code.Value, string.Empty);
        var applied = part.Update(description, categoryId, uomId, weightKg, unitCost, isConditionBased, manufacturerId);
        return applied.IsFailure ? applied.Error : part;
    }

    /// <summary>Changes everything but the brand and the OEM number; <see langword="true"/> if anything changed.</summary>
    public Result<bool> Update(
        string? description, int categoryId, short uomId, decimal? weightKg, decimal unitCost, bool isConditionBased, short? manufacturerId)
    {
        if ((description?.Trim().Length ?? 0) < MinDescriptionLength)
        {
            return InvalidDescription;
        }

        if (unitCost < 0)
        {
            return InvalidCost;
        }

        if (weightKg < 0)
        {
            return InvalidWeight;
        }

        var renamed = Rename(WhiteSpace().Replace(description!.Trim(), " "), MaxDescriptionLength);
        if (renamed.IsFailure)
        {
            return InvalidDescription;
        }

        var changed = renamed.Value || CategoryId != categoryId || UomId != uomId || WeightKg != weightKg || UnitCost != unitCost
                      || IsConditionBased != isConditionBased || ManufacturerId != manufacturerId;
        CategoryId = categoryId;
        UomId = uomId;
        WeightKg = weightKg;
        UnitCost = unitCost;
        IsConditionBased = isConditionBased;
        ManufacturerId = manufacturerId;
        return changed;
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9.\-/]{2,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex OemPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();
}

/// <summary>Platform reference table <c>ref.part_category</c>, read-only here (a part's category).</summary>
internal sealed class PartCategory
{
    public int Id { get; private set; }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;
}

/// <summary>Platform reference table <c>ref.unit_of_measure</c>, read-only here (a part's unit).</summary>
internal sealed class UnitOfMeasure
{
    public short Id { get; private set; }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;
}
