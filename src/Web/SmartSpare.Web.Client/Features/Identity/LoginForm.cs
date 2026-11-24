using FluentValidation;
using SmartSpare.ApiContracts.Identity;

namespace SmartSpare.Web.Client.Features.Identity;

/// <summary>The editable model of the sign-in form; <see cref="ToRequest"/> gives the shared <see cref="LoginRequest"/>.</summary>
public sealed class LoginForm
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    public LoginRequest ToRequest() => new(UserName.Trim(), Password, RememberMe);
}

/// <summary>
/// The shared <see cref="LoginRequestValidator"/> applied to the form, field by field, as MudForm expects
/// (<c>Validation</c> with a model and a property name). The server runs the same validator (STAGE_3_PLAN 8.3).
/// </summary>
public sealed class LoginFormValidator
{
    private readonly LoginRequestValidator _validator = new();

    public Func<object, string, Task<IEnumerable<string>>> ValidateValue => async (model, propertyName) =>
    {
        var request = ((LoginForm)model).ToRequest();
        var result = await _validator.ValidateAsync(
            ValidationContext<LoginRequest>.CreateWithOptions(request, options => options.IncludeProperties(propertyName))).ConfigureAwait(false);
        return result.IsValid ? [] : result.Errors.Select(e => e.ErrorMessage);
    };
}
