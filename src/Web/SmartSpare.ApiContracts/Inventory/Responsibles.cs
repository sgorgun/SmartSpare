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

/// <summary>A brand (the Platform's brand list until the brand catalogue of PR 7h).</summary>
[Description("A brand.")]
public sealed record BrandDto(
    [property: Description("Brand id.")] short Id,
    [property: Description("Brand code.")] string Code,
    [property: Description("Brand name.")] string Name);
