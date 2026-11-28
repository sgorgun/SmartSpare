using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory.Domain.Catalog;

/// <summary>
/// The maker of a part (catalogue <c>catalog.manufacturer</c>, STAGE_7_PLAN 3.1, PR 7h3), for example MANN+HUMMEL for a
/// filter sold under a machinery brand. A part may name one; a manufacturer with active parts cannot be archived.
/// </summary>
internal sealed partial class Manufacturer : MasterRecord<short>
{
    public const int MaxWebsiteLength = 200;

    public static readonly Error InvalidCode = Error.Validation("Inventory.InvalidManufacturerCode", "A manufacturer code has 2 to 20 capital letters, digits or underscores.");

    public static readonly Error InvalidCountry = Error.Validation("Inventory.InvalidManufacturerCountry", "The country is an ISO 3166-1 alpha-2 code, e.g. DE, or empty.");

    public static readonly Error InvalidWebsite = Error.Validation("Inventory.InvalidManufacturerWebsite", $"The website is an http(s) address of at most {MaxWebsiteLength} characters, or empty.");

    private Manufacturer()
    {
    }

    private Manufacturer(string code, string name, string? countryCode, string? website)
        : base(code, name)
    {
        CountryCode = countryCode;
        Website = website;
    }

    /// <summary>ISO 3166-1 alpha-2 country, if known.</summary>
    public string? CountryCode { get; private set; }

    /// <summary>The manufacturer's website, if known.</summary>
    public string? Website { get; private set; }

    public static Result<string> NormaliseCode(string? code) => MasterRecordRules.NormaliseCode(code, CodePattern(), InvalidCode);

    public static Result<Manufacturer> Create(string? code, string? name, string? countryCode, string? website)
    {
        var normalisedCode = NormaliseCode(code);
        if (normalisedCode.IsFailure)
        {
            return normalisedCode.Error;
        }

        var normalisedName = MasterRecordRules.NormaliseName(name);
        if (normalisedName.IsFailure)
        {
            return normalisedName.Error;
        }

        var country = NormaliseCountry(countryCode);
        if (country.IsFailure)
        {
            return country.Error;
        }

        var site = NormaliseWebsite(website);
        return site.IsFailure ? site.Error : new Manufacturer(normalisedCode.Value, normalisedName.Value, country.Value, site.Value);
    }

    /// <summary>Changes name, country and website; <see langword="true"/> if anything changed.</summary>
    public Result<bool> Update(string? name, string? countryCode, string? website)
    {
        var country = NormaliseCountry(countryCode);
        if (country.IsFailure)
        {
            return country.Error;
        }

        var site = NormaliseWebsite(website);
        if (site.IsFailure)
        {
            return site.Error;
        }

        var renamed = Rename(name);
        if (renamed.IsFailure)
        {
            return renamed.Error;
        }

        var changed = renamed.Value || CountryCode != country.Value || Website != site.Value;
        CountryCode = country.Value;
        Website = site.Value;
        return changed;
    }

    private static Result<string?> NormaliseCountry(string? countryCode)
    {
        var country = countryCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(country))
        {
            return (string?)null;
        }

        return CountryPattern().IsMatch(country) ? country : InvalidCountry;
    }

    private static Result<string?> NormaliseWebsite(string? website)
    {
        var site = website?.Trim();
        if (string.IsNullOrEmpty(site))
        {
            return (string?)null;
        }

        return site.Length <= MaxWebsiteLength && Uri.TryCreate(site, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? site
            : InvalidWebsite;
    }

    [GeneratedRegex("^[A-Z0-9_]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPattern();
}
