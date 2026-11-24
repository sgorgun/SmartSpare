using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>Expected business errors of the Identity module.</summary>
internal static class IdentityErrors
{
    public static readonly Error InvalidUserName = Error.Validation(
        "Identity.InvalidUserName", "A user name has 3 to 50 characters: letters, digits, '.', '_' or '-'.");

    public static readonly Error InvalidEmailAddress = Error.Validation(
        "Identity.InvalidEmailAddress", "The e-mail address is not valid.");

    public static readonly Error UserInactive = Error.BusinessRule(
        "Identity.UserInactive", "The user is deactivated.");
}
