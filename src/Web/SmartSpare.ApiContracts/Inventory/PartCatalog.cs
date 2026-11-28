using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.MasterData;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>
/// A row of <c>GET /api/v1/inventory/parts</c>, the parts catalogue (PR 7i1): the part with its reference codes and its
/// version. The first fields are those of the former search row, so pickers read it unchanged.
/// </summary>
[Description("A catalogue part without its stock.")]
public sealed record PartDto(
    [property: Description("Part id.")] long Id,
    [property: Description("Brand code, for example CLAAS or JD.")] string BrandCode,
    [property: Description("Manufacturer (OEM) part number, upper case; unique per brand.")] string OemPartNumber,
    [property: Description("Description of the part.")] string Description,
    [property: Description("Name of the part category.")] string CategoryName,
    [property: Description("Unit of measure code: PCS, SET, L, KG or M.")] string UnitOfMeasure,
    [property: Description("Standard cost per unit in EUR.")] decimal UnitCost,
    [property: Description("Brand id (fixed after creation).")] short BrandId,
    [property: Description("Part category id.")] int CategoryId,
    [property: Description("Part category code, for example OIL_FILTER.")] string CategoryCode,
    [property: Description("Unit of measure id.")] short UomId,
    [property: Description("Weight in kilograms, if known.")] decimal? WeightKg,
    [property: Description("True for parts replaced as required (air and cab filters): their demand comes only from forecasts.")] bool IsConditionBased,
    [property: Description("Manufacturer id, if known.")] short? ManufacturerId,
    [property: Description("Manufacturer code, if known, for example MANN.")] string? ManufacturerCode,
    [property: Description("Archived parts are kept for history and cannot be chosen.")] bool IsArchived,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A new or changed part. Code is the OEM number and name the description (the shared catalogue shape).</summary>
[Description("A new or changed part; code is the OEM part number and name the description.")]
public sealed record PartWrite(
    [property: Description("OEM part number: 3 to 40 letters, digits, '.', '-' or '/'; stored upper case. Fixed after creation.")] string Code,
    [property: Description("Description, 3 to 300 characters.")] string Name,
    [property: Description("Brand id; with the OEM number the part's key, fixed after creation. An archived brand is refused (SS500).")] short BrandId,
    [property: Description("Part category id (ref.part_category).")] int CategoryId,
    [property: Description("Unit of measure id (ref.unit_of_measure).")] short UomId,
    [property: Description("Weight in kilograms, or empty.")] decimal? WeightKg,
    [property: Description("Standard cost per unit in EUR, zero or more.")] decimal UnitCost,
    [property: Description("Replaced as required: demand only from forecasts.")] bool IsConditionBased,
    [property: Description("Manufacturer id, or empty to clear it. An archived manufacturer is refused (SS500).")] short? ManufacturerId) : ICatalogWrite;

/// <summary>The rules of <see cref="PartWrite"/>, the same on the form and the server.</summary>
public sealed class PartWriteValidator : AbstractValidator<PartWrite>
{
    public PartWriteValidator()
    {
        RuleFor(p => p.Code).NotEmpty().Matches(@"^\s*[A-Za-z0-9][A-Za-z0-9.\-/ ]{2,39}\s*$")
            .WithMessage("Use 3 to 40 letters, digits, '.', '-' or '/', e.g. RE504836.");
        RuleFor(p => p.Name).NotEmpty().Must(n => n.Trim().Length is >= 3 and <= 300).WithMessage("The description has 3 to 300 characters.");
        RuleFor(p => p.BrandId).GreaterThan((short)0).WithMessage("Choose a brand.");
        RuleFor(p => p.CategoryId).GreaterThan(0).WithMessage("Choose a category.");
        RuleFor(p => p.UomId).GreaterThan((short)0).WithMessage("Choose a unit of measure.");
        RuleFor(p => p.UnitCost).GreaterThanOrEqualTo(0).LessThan(10_000_000_000m);
        RuleFor(p => p.WeightKg).GreaterThanOrEqualTo(0).LessThan(1_000_000m).When(p => p.WeightKg is not null);
    }
}

/// <summary>An entry of the Platform's reference data (part category, unit of measure) for a choice in a form.</summary>
[Description("An entry of the reference data: a part category or a unit of measure.")]
public sealed record ReferenceItem(
    [property: Description("Id.")] long Id,
    [property: Description("Business code, for example OIL_FILTER or PCS.")] string Code,
    [property: Description("Display name.")] string Name);
