using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.MasterData;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>A manufacturer of parts (catalogue, ADR 0025).</summary>
[Description("Who makes a part, e.g. MANN+HUMMEL.")]
public sealed record ManufacturerDto(
    [property: Description("Manufacturer id.")] short Id,
    [property: Description("Business key, e.g. MANN.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("ISO 3166-1 alpha-2 country, if known.")] string? CountryCode,
    [property: Description("Website, if known.")] string? Website,
    [property: Description("Archived manufacturers stay on parts but cannot be chosen.")] bool IsArchived,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>Body of creating or changing a manufacturer.</summary>
[Description("A manufacturer to create or change.")]
public sealed record ManufacturerWrite(
    [property: Description("Business key: 2 to 20 capital letters, digits or underscores. Fixed after creation except for an administrator.")] string Code,
    [property: Description("Display name, up to 200 characters.")] string Name,
    [property: Description("ISO 3166-1 alpha-2 country, e.g. DE, or empty.")] string? CountryCode,
    [property: Description("http(s) website, or empty.")] string? Website) : ICatalogWrite;

public sealed class ManufacturerWriteValidator : AbstractValidator<ManufacturerWrite>
{
    public ManufacturerWriteValidator()
    {
        RuleFor(m => m.Code).NotEmpty().Matches("^[A-Za-z0-9_]{2,20}$").WithMessage("Use 2 to 20 letters, digits or underscores, e.g. MANN.");
        RuleFor(m => m.Name).NotEmpty().MaximumLength(200);
        RuleFor(m => m.CountryCode).Matches("^[A-Za-z]{2}$").When(m => !string.IsNullOrWhiteSpace(m.CountryCode))
            .WithMessage("Use the two-letter country code, e.g. DE, or leave it empty.");
        RuleFor(m => m.Website).MaximumLength(200)
            .Must(w => Uri.TryCreate(w.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .When(m => !string.IsNullOrWhiteSpace(m.Website)).WithMessage("Use an http(s) address, e.g. https://www.mann-hummel.com, or leave it empty.");
    }
}
