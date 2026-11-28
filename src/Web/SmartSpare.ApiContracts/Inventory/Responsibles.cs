using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>An assignment role of a responsible employee for one record kind (ADR 0026).</summary>
[Description("An assignment role of a responsible employee; roles that grant scope give rights (brand approval scope, hub access).")]
public sealed record AssignmentRoleDto(
    [property: Description("brand, warehouse, manufacturer, part or supplier.")] string RecordKind,
    [property: Description("Role code, e.g. KEEPER.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("Whether the role gives a right; such assignments need the Administration policy.")] bool GrantsScope);

/// <summary>A responsible employee of a record.</summary>
[Description("A responsible employee of a brand or warehouse with the assignment role.")]
public sealed record ResponsibleDto(
    [property: Description("Employee id.")] Guid EmployeeId,
    [property: Description("Employee number.")] string EmployeeCode,
    [property: Description("Employee name.")] string EmployeeName,
    [property: Description("False if the employee has left; the assignment then has no effect.")] bool EmployeeActive,
    [property: Description("Role code.")] string Role,
    [property: Description("Role name.")] string RoleName,
    [property: Description("Whether the role gives a right.")] bool GrantsScope);

/// <summary>One employee in one role, in a replacement of a record's responsibles.</summary>
[Description("One employee in one assignment role.")]
public sealed record ResponsibleEntry(
    [property: Description("Employee id (an active employee).")] Guid EmployeeId,
    [property: Description("Role code of this record kind.")] string Role);

/// <summary>The complete new set of responsible employees of a record.</summary>
[Description("The complete new set of responsible employees of a record; missing pairs are removed.")]
public sealed record ResponsiblesRequest([property: Description("Employee and role pairs.")] IReadOnlyList<ResponsibleEntry> Responsibles);

public sealed class ResponsiblesRequestValidator : AbstractValidator<ResponsiblesRequest>
{
    public const int MaxEntries = 50;

    public ResponsiblesRequestValidator()
    {
        RuleFor(r => r.Responsibles).NotNull().Must(e => e is null || e.Count <= MaxEntries).WithMessage($"At most {MaxEntries} responsible employees.");
        RuleForEach(r => r.Responsibles).ChildRules(e =>
        {
            e.RuleFor(x => x.EmployeeId).NotEmpty();
            e.RuleFor(x => x.Role).NotEmpty().MaximumLength(30);
        });
    }
}

/// <summary>A brand (Inventory's catalogue since PR 7h, ADR 0025).</summary>
[Description("A machinery brand.")]
public sealed record BrandDto(
    [property: Description("Brand id; the same id in every module's copy.")] short Id,
    [property: Description("Business key, e.g. CLAAS.")] string Code,
    [property: Description("Display name, up to 80 characters.")] string Name,
    [property: Description("ISO 3166-1 alpha-2 country of the manufacturer, e.g. DE.")] string CountryCode,
    [property: Description("Archived brands stay on parts and history but get no new parts or assignments.")] bool IsArchived,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>Body of creating or changing a brand.</summary>
[Description("A brand to create or change.")]
public sealed record BrandWrite(
    [property: Description("Business key: 2 to 10 capital letters, digits or underscores. Fixed after creation except for an administrator.")] string Code,
    [property: Description("Display name, up to 80 characters.")] string Name,
    [property: Description("ISO 3166-1 alpha-2 country, e.g. DE.")] string CountryCode) : MasterData.ICatalogWrite;

public sealed class BrandWriteValidator : FluentValidation.AbstractValidator<BrandWrite>
{
    public BrandWriteValidator()
    {
        RuleFor(b => b.Code).NotEmpty().Matches("^[A-Za-z0-9_]{2,10}$").WithMessage("Use 2 to 10 letters, digits or underscores, e.g. CLAAS.");
        RuleFor(b => b.Name).NotEmpty().MaximumLength(80);
        RuleFor(b => b.CountryCode).NotEmpty().Matches("^[A-Za-z]{2}$").WithMessage("Use the two-letter country code, e.g. DE.");
    }
}
