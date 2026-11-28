using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>A role: a named bundle of permissions (ADR 0028).</summary>
[Description("A role: a named bundle of permissions.")]
public sealed record RoleSummary(
    [property: Description("Role id.")] short Id,
    [property: Description("Business key, e.g. INVENTORY_PLANNER.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("What the role is for.")] string? Description,
    [property: Description("The permissions of the role.")] IReadOnlyList<string> Permissions,
    [property: Description("A built-in role that cannot be edited, archived or deleted.")] bool IsLocked,
    [property: Description("Archived roles cannot be put in a role template.")] bool IsArchived,
    [property: Description("Codes of the positions whose template holds the role.")] IReadOnlyList<string> Positions,
    [property: Description("A seeded role shipped with a default permission set, which reset-to-default restores.")] bool HasDefault,
    [property: Description("Whether the permissions equal the shipped default set (false for roles without one).")] bool IsDefault,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A new role.</summary>
[Description("A new role with its permissions.")]
public sealed record CreateRoleRequest(
    [property: Description("Business key: 3 to 30 capital letters or '_'.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("What the role is for; optional.")] string? Description,
    [property: Description("Permissions of the catalogue.")] IReadOnlyList<string> Permissions);

/// <summary>Changed name, description or permissions of a role.</summary>
[Description("Changed name, description or permissions of a role; the code never changes.")]
public sealed record UpdateRoleRequest(
    [property: Description("Display name.")] string Name,
    [property: Description("What the role is for; optional.")] string? Description,
    [property: Description("Permissions of the catalogue.")] IReadOnlyList<string> Permissions);

/// <summary>An editable copy of a role.</summary>
[Description("A new role with the permissions of an existing one.")]
public sealed record CopyRoleRequest(
    [property: Description("Business key of the copy: 3 to 30 capital letters or '_'.")] string Code,
    [property: Description("Display name of the copy.")] string Name);

/// <summary>A permission of the catalogue for the roles screen.</summary>
[Description("A permission of the catalogue.")]
public sealed record PermissionDto(
    [property: Description("Name, e.g. Inventory.Transfer.Approve.")] string Name,
    [property: Description("Module, for grouping.")] string Module,
    [property: Description("What it allows.")] string Description);

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches(OrganisationLimits.RoleCodePattern).WithMessage("A role code has 3 to 30 capital letters or '_'.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationLimits.MaxRoleNameLength);
        RuleFor(r => r.Description).MaximumLength(OrganisationLimits.MaxDescriptionLength);
        RuleFor(r => r.Permissions).NotNull().Must(UpdateRoleRequestValidator.KnownPermissions).WithMessage("Unknown permission.");
    }
}

public sealed class CopyRoleRequestValidator : AbstractValidator<CopyRoleRequest>
{
    public CopyRoleRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().Matches(OrganisationLimits.RoleCodePattern).WithMessage("A role code has 3 to 30 capital letters or '_'.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationLimits.MaxRoleNameLength);
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(OrganisationLimits.MaxRoleNameLength);
        RuleFor(r => r.Description).MaximumLength(OrganisationLimits.MaxDescriptionLength);
        RuleFor(r => r.Permissions).NotNull().Must(KnownPermissions).WithMessage("Unknown permission.");
    }

    internal static bool KnownPermissions(IReadOnlyList<string>? permissions) => permissions is null || permissions.All(Authorization.Permissions.Exists);
}
