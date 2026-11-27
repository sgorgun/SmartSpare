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
    [property: Description("Effective role codes (from the position), for example INVENTORY_PLANNER.")] IReadOnlyList<string> Roles,
    [property: Description("Effective brand codes of the approval scope.")] IReadOnlyList<string> Brands,
    [property: Description("Last successful sign-in.")] DateTimeOffset? LastLoginAt,
    [property: Description("The employee the account belongs to.")] Guid EmployeeId,
    [property: Description("Employee number.")] string EmployeeCode,
    [property: Description("Name of the employee's position.")] string PositionName,
    [property: Description("Every effective right with its origin (ADR 0026).")] IReadOnlyList<EffectiveRightDto> Rights,
    [property: Description("Facts that give no right, e.g. a brand assignment without the BRAND_MANAGER role.")] IReadOnlyList<EffectiveRightDto> WithoutEffect,
    [property: Description("Why the account has no rights at all, if so.")] string? InactiveReason);

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
