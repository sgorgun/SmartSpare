using FluentValidation;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>
/// Shape of a sign-in request, shared by the sign-in form and <c>POST /bff/login</c>. It checks only presence and
/// length: whether the account exists is never revealed (the server answers every failure with
/// <see cref="ProblemCodes.InvalidCredentials"/>).
/// </summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    /// <summary>Longest user name accepted by <c>ck_app_user_user_name</c>.</summary>
    public const int MaxUserNameLength = 50;

    /// <summary>Upper bound that keeps PBKDF2 hashing of hostile input cheap; far above any real password.</summary>
    public const int MaxPasswordLength = 256;

    public LoginRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().MaximumLength(MaxUserNameLength);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(MaxPasswordLength);
    }
}
