using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory.Domain.Catalog;

/// <summary>
/// A machinery brand (catalogue <c>catalog.brand</c>, moved from <c>ref.brand</c> with its ids, STAGE_7_PLAN 3.2, decision
/// C1). Parts, brand responsibles and the brand scope of approvals refer to it by id; Fleet and Identity keep read-only
/// copies fed by <c>BrandCatalogChanged</c>. Names stay within 80 characters, the width the views over the table use.
/// </summary>
internal sealed partial class Brand : MasterRecord<short>
{
    public const int MaxNameLength = 80;

    public static readonly Error InvalidCode = Error.Validation("Inventory.InvalidBrandCode", "A brand code has 2 to 10 capital letters, digits or underscores.");

    public static readonly Error InvalidCountry = Error.Validation("Inventory.InvalidBrandCountry", "The country is an ISO 3166-1 alpha-2 code, e.g. DE.");

    public static readonly Error NameTooLong = Error.Validation("Inventory.BrandNameTooLong", $"A brand name has at most {MaxNameLength} characters.");

    private Brand()
    {
        CountryCode = null!;
    }

    private Brand(string code, string name, string countryCode)
        : base(code, name)
    {
        CountryCode = countryCode;
    }

    /// <summary>ISO 3166-1 alpha-2 country of the manufacturer.</summary>
    public string CountryCode { get; private set; }

    public static Result<string> NormaliseCode(string? code) => MasterRecordRules.NormaliseCode(code, CodePattern(), InvalidCode);

    public static Result<Brand> Create(string? code, string? name, string? countryCode)
    {
        var normalisedCode = NormaliseCode(code);
        if (normalisedCode.IsFailure)
        {
            return normalisedCode.Error;
        }

        var normalisedName = NormaliseName(name);
        if (normalisedName.IsFailure)
        {
            return normalisedName.Error;
        }

        var country = NormaliseCountry(countryCode);
        return country.IsFailure ? country.Error : new Brand(normalisedCode.Value, normalisedName.Value, country.Value);
    }

    /// <summary>Changes name and country; <see langword="true"/> if anything changed.</summary>
    public Result<bool> Update(string? name, string? countryCode)
    {
        var normalisedName = NormaliseName(name);
        if (normalisedName.IsFailure)
        {
            return normalisedName.Error;
        }

        var country = NormaliseCountry(countryCode);
        if (country.IsFailure)
        {
            return country.Error;
        }

        var renamed = Rename(normalisedName.Value);
        if (renamed.IsFailure)
        {
            return renamed.Error;
        }

        var changed = renamed.Value || CountryCode != country.Value;
        CountryCode = country.Value;
        return changed;
    }

    private static Result<string> NormaliseName(string? name)
    {
        var normalised = MasterRecordRules.NormaliseName(name);
        return normalised.IsSuccess && normalised.Value.Length > MaxNameLength ? NameTooLong : normalised;
    }

    private static Result<string> NormaliseCountry(string? countryCode)
    {
        var country = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        return CountryPattern().IsMatch(country) ? country : InvalidCountry;
    }

    [GeneratedRegex("^[A-Z0-9_]{2,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPattern();
}
