using System.ComponentModel;
using FluentValidation;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>Limits of the user administration (the columns and CHECKs of <c>iam.app_user</c>, STAGE_3_PLAN 2.7).</summary>
public static class UserLimits
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MinPasswordLength = 12;

    public const int MaxPasswordLength = 128;

    public const int MaxNameLength = 150;
}

/// <summary>A user in the administration list; personal data masked.</summary>
[Description("A user in the administration list; personal data masked.")]
public sealed record UserSummary(
    [property: Description("User id.")] Guid Id,
    [property: Description("Sign-in name.")] string UserName,
    [property: Description("Full name, masked to initials.")] string? FullNameMasked,
    [property: Description("E-mail, partly hidden.")] string? EmailMasked,
    [property: Description("Whether the user may sign in.")] bool IsActive,
    [property: Description("Whether the account is locked out after failed sign-ins now.")] bool IsLockedOut,
    [property: Description("Whether the personal data was anonymised (GDPR); such a user is inactive for good.")] bool IsAnonymised,
    [property: Description("Home hub of a warehouse keeper, if any.")] short? HomeWarehouseId,
    [property: Description("Role codes, for example INVENTORY_PLANNER.")] IReadOnlyList<string> Roles,
    [property: Description("Brand codes a brand manager is responsible for.")] IReadOnlyList<string> Brands,
    [property: Description("Last successful sign-in.")] DateTimeOffset? LastLoginAt);

/// <summary>What the user form can choose from: roles, brands and hubs.</summary>
[Description("What the user form can choose from: roles, brands and hubs.")]
public sealed record UserFormOptions(
    [property: Description("Every role code.")] IReadOnlyList<string> Roles,
    [property: Description("Every brand code.")] IReadOnlyList<string> Brands,
    [property: Description("Active hubs: id and code.")] IReadOnlyList<HubOption> Hubs);

/// <summary>A hub a keeper can be assigned to.</summary>
[Description("A hub a keeper can be assigned to.")]
public sealed record HubOption(
    [property: Description("Hub id.")] short Id,
    [property: Description("Hub code.")] string Code,
    [property: Description("Hub name.")] string Name);

/// <summary>Roles, brands and home hub of a user.</summary>
[Description("Roles, brands and home hub of a user.")]
public sealed record UserAccessRequest(
    [property: Description("Role codes; at least one.")] IReadOnlyList<string> Roles,
    [property: Description("Brand codes for a brand manager; empty for other roles.")] IReadOnlyList<string> Brands,
    [property: Description("Home hub id for a warehouse keeper; empty for no hub.")] short? HomeWarehouseId);

public sealed class UserAccessRequestValidator : AbstractValidator<UserAccessRequest>
{
    public UserAccessRequestValidator()
    {
        RuleFor(r => r.Roles).NotEmpty().WithMessage("Choose at least one role.")
            .Must(roles => roles is null || roles.All(AppRoles.All.Contains)).WithMessage("Unknown role.");
        RuleFor(r => r.Brands).NotNull();
        RuleFor(r => r.Brands).Must(b => b.Count == 0).When(r => r.Roles is not null && !r.Roles.Contains(AppRoles.BrandManager))
            .WithMessage("Brands are only assigned to brand managers.");
        RuleFor(r => r.HomeWarehouseId).GreaterThan((short)0).When(r => r.HomeWarehouseId is not null);
    }
}

/// <summary>A new user with an initial password and its access.</summary>
[Description("A new user with an initial password and its access.")]
public sealed record CreateUserRequest(
    [property: Description("Sign-in name: 3 to 50 letters, digits, '.', '_' or '-'; stored in lower case.")] string UserName,
    [property: Description("E-mail address.")] string Email,
    [property: Description("Full name.")] string FullName,
    [property: Description("Phone number, optional.")] string? Phone,
    [property: Description("Initial password: at least 12 characters, 4 different ones.")] string Password,
    [property: Description("Roles, brands and home hub.")] UserAccessRequest Access);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().Matches("^[A-Za-z0-9._-]{3,50}$")
            .WithMessage("A user name has 3 to 50 characters: letters, digits, '.', '_' or '-'.");
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(UserLimits.MaxNameLength);
        RuleFor(r => r.Phone).MaximumLength(30);
        RuleFor(r => r.Password).SetValidator(new PasswordRule());
        RuleFor(r => r.Access).NotNull().SetValidator(new UserAccessRequestValidator());
    }
}

/// <summary>A user's own password change.</summary>
[Description("A user's own password change.")]
public sealed record ChangePasswordRequest(
    [property: Description("The current password.")] string CurrentPassword,
    [property: Description("The new password: at least 12 characters, 4 different ones.")] string NewPassword);

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty();
        RuleFor(r => r.NewPassword).SetValidator(new PasswordRule())
            .NotEqual(r => r.CurrentPassword).WithMessage("The new password must differ from the current one.");
    }
}

/// <summary>The password policy of STAGE_3_PLAN 2.7, the same as the server's Identity options: length over classes.</summary>
public sealed class PasswordRule : AbstractValidator<string>
{
    public PasswordRule()
    {
        RuleFor(p => p).NotEmpty().WithName("Password")
            .MinimumLength(UserLimits.MinPasswordLength).WithMessage($"A password has at least {UserLimits.MinPasswordLength} characters.")
            .MaximumLength(UserLimits.MaxPasswordLength)
            .Must(p => p is null || p.Distinct().Count() >= 4).WithMessage("A password uses at least 4 different characters.");
    }
}
