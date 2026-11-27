using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>Limits of positions and employees (the columns and CHECKs of <c>iam.position</c> and <c>iam.employee</c>).</summary>
public static class OrganisationLimits
{
    public const int MaxPositionNameLength = 120;

    public const int MaxDescriptionLength = 500;

    public const int MaxEmployeeNameLength = 150;

    public const string PositionCodePattern = "^[A-Za-z0-9_]{2,30}$";

    public const string EmployeeCodePattern = "^[Ee][0-9]{4,6}$";
}

/// <summary>A job position and its role template.</summary>
[Description("A job position and its role template: every employee of the position has exactly these roles.")]
public sealed record PositionSummary(
    [property: Description("Position id.")] short Id,
    [property: Description("Business key, e.g. BRAND_MANAGER.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("What the position does.")] string? Description,
    [property: Description("Role codes of the template.")] IReadOnlyList<string> Roles,
    [property: Description("Whether the position is archived (cannot be chosen).")] bool IsArchived,
    [property: Description("Active employees in the position.")] int ActiveEmployees,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A new position.</summary>
[Description("A new position with its role template.")]
public sealed record CreatePositionRequest(
    [property: Description("Business key: 2 to 30 letters, digits or '_'; stored in capitals.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("What the position does; optional.")] string? Description,
    [property: Description("Role codes of the template; may be empty for staff without system roles.")] IReadOnlyList<string> Roles);

/// <summary>Changed name, description or role template of a position.</summary>
[Description("Changed name, description or role template of a position; the code never changes.")]
public sealed record UpdatePositionRequest(
    [property: Description("Display name.")] string Name,
    [property: Description("What the position does; optional.")] string? Description,
    [property: Description("Role codes of the template.")] IReadOnlyList<string> Roles);

public sealed class CreatePositionRequestValidator : AbstractValidator<CreatePositionRequest>
{
    public CreatePositionRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches(OrganisationLimits.PositionCodePattern)
            .WithMessage("A position code has 2 to 30 letters, digits or '_'.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationLimits.MaxPositionNameLength);
        RuleFor(r => r.Description).MaximumLength(OrganisationLimits.MaxDescriptionLength);
        RuleFor(r => r.Roles).NotNull().Must(KnownRoles).WithMessage("Unknown role.");
    }

    internal static bool KnownRoles(IReadOnlyList<string>? roles) => roles is null || roles.All(AppRoles.All.Contains);
}

public sealed class UpdatePositionRequestValidator : AbstractValidator<UpdatePositionRequest>
{
    public UpdatePositionRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationLimits.MaxPositionNameLength);
        RuleFor(r => r.Description).MaximumLength(OrganisationLimits.MaxDescriptionLength);
        RuleFor(r => r.Roles).NotNull().Must(CreatePositionRequestValidator.KnownRoles).WithMessage("Unknown role.");
    }
}

/// <summary>An employee of the dealership, as the administration sees it (personal data in full).</summary>
[Description("An employee of the dealership; personal data in full (policy Administration).")]
public sealed record EmployeeSummary(
    [property: Description("Employee id.")] Guid Id,
    [property: Description("Employee number, e.g. E0001.")] string Code,
    [property: Description("Full name.")] string FullName,
    [property: Description("Corporate e-mail, optional.")] string? Email,
    [property: Description("Phone, optional.")] string? Phone,
    [property: Description("Position id.")] short PositionId,
    [property: Description("Position code.")] string PositionCode,
    [property: Description("Position name.")] string PositionName,
    [property: Description("Hiring date, optional.")] DateOnly? HiredOn,
    [property: Description("Leaving date of an employee who left.")] DateOnly? LeftOn,
    [property: Description("Whether the employee left (archived).")] bool IsArchived,
    [property: Description("Whether the personal data was anonymised (GDPR).")] bool IsAnonymised,
    [property: Description("User name of the employee's account, if there is one.")] string? AccountUserName,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A new employee.</summary>
[Description("A new employee.")]
public sealed record CreateEmployeeRequest(
    [property: Description("Employee number: 'E' and 4 to 6 digits.")] string Code,
    [property: Description("Full name.")] string FullName,
    [property: Description("Corporate e-mail, optional.")] string? Email,
    [property: Description("Phone, optional.")] string? Phone,
    [property: Description("Position id; its role template gives the account's roles.")] short PositionId,
    [property: Description("Hiring date, optional.")] DateOnly? HiredOn);

/// <summary>Changed data or position of an employee.</summary>
[Description("Changed data or position of an employee; the number never changes. A new position changes the account's roles at once.")]
public sealed record UpdateEmployeeRequest(
    [property: Description("Full name.")] string FullName,
    [property: Description("Corporate e-mail, optional.")] string? Email,
    [property: Description("Phone, optional.")] string? Phone,
    [property: Description("Position id.")] short PositionId,
    [property: Description("Hiring date, optional.")] DateOnly? HiredOn);

/// <summary>An employee leaves the company.</summary>
[Description("An employee leaves the company: archived, the account can no longer sign in.")]
public sealed record ArchiveEmployeeRequest([property: Description("Leaving date.")] DateOnly LeftOn);

/// <summary>A user account for an employee.</summary>
[Description("A user account for an employee: the employee's position gives its roles.")]
public sealed record CreateAccountRequest(
    [property: Description("Sign-in name: 3 to 50 letters, digits, '.', '_' or '-'; stored in lower case.")] string UserName,
    [property: Description("Initial password: at least 12 characters, 4 different ones.")] string Password);

public sealed class CreateEmployeeRequestValidator : AbstractValidator<CreateEmployeeRequest>
{
    public CreateEmployeeRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches(OrganisationLimits.EmployeeCodePattern)
            .WithMessage("An employee number is 'E' followed by 4 to 6 digits, for example E0042.");
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(OrganisationLimits.MaxEmployeeNameLength);
        RuleFor(r => r.Email).EmailAddress().MaximumLength(254).When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.Phone).MaximumLength(30);
        RuleFor(r => r.PositionId).GreaterThan((short)0).WithMessage("Choose a position.");
    }
}

public sealed class UpdateEmployeeRequestValidator : AbstractValidator<UpdateEmployeeRequest>
{
    public UpdateEmployeeRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(OrganisationLimits.MaxEmployeeNameLength);
        RuleFor(r => r.Email).EmailAddress().MaximumLength(254).When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.Phone).MaximumLength(30);
        RuleFor(r => r.PositionId).GreaterThan((short)0).WithMessage("Choose a position.");
    }
}

public sealed class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    public CreateAccountRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().Matches("^[A-Za-z0-9._-]{3,50}$")
            .WithMessage("A user name has 3 to 50 characters: letters, digits, '.', '_' or '-'.");
        RuleFor(r => r.Password).SetValidator(new PasswordRule());
    }
}

/// <summary>One effective right of an account with its origin.</summary>
[Description("One effective right of an account with where it comes from.")]
public sealed record EffectiveRightDto(
    [property: Description("Role, Brand or Hub.")] string Kind,
    [property: Description("Role code, brand code or hub code.")] string Value,
    [property: Description("Position, BrandAssignment or WarehouseAssignment.")] string Origin,
    [property: Description("The position or assignment it comes from, e.g. 'Brand manager'.")] string OriginName);
