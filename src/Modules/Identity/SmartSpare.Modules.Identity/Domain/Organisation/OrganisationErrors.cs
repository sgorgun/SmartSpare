using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain.Organisation;

/// <summary>Expected business errors of positions and employees.</summary>
internal static class OrganisationErrors
{
    public static readonly Error InvalidPositionCode = Error.Validation(
        "Identity.InvalidPositionCode", "A position code has 2 to 30 capital letters, digits or '_'.");

    public static readonly Error InvalidEmployeeCode = Error.Validation(
        "Identity.InvalidEmployeeCode", "An employee number is 'E' followed by 4 to 6 digits, for example E0042.");

    public static readonly Error InvalidName = Error.Validation("Identity.InvalidName", "The name is required and must not be too long.");

    public static readonly Error InvalidDescription = Error.Validation("Identity.InvalidDescription", "The description is too long.");

    public static readonly Error LeftBeforeHired = Error.Validation("Identity.LeftBeforeHired", "The leaving date cannot be before the hiring date.");

    public static readonly Error EmployeeAnonymised = Error.BusinessRule(
        "Identity.EmployeeAnonymised", "The employee was anonymised and cannot be changed.");

    public static readonly Error PositionNotFound = Error.NotFound("Identity.PositionNotFound", "The position does not exist.");

    public static readonly Error PositionArchived = Error.BusinessRule("SS500", "The position is archived and cannot be chosen.");

    public static readonly Error EmployeeNotFound = Error.NotFound("Identity.EmployeeNotFound", "The employee does not exist.");

    public static readonly Error CodeTaken = Error.Conflict("Identity.CodeTaken", "This code is already used.");

    public static readonly Error EmailTaken = Error.Conflict("Identity.EmailTaken", "This e-mail address is already used by another employee.");

    public static readonly Error UnknownRole = Error.Validation("Identity.UnknownRole", "A role code is unknown.");

    public static readonly Error PositionInUse = Error.Conflict(
        "SS501", "The position cannot be archived while active employees hold it.");

    public static readonly Error LastAdministrator = Error.Conflict(
        "Identity.LastAdministrator", "This change would leave SmartSpare without an active administrator.");

    public static readonly Error AccountExists = Error.Conflict("Identity.AccountExists", "The employee already has a user account.");

    public static readonly Error EmployeeInactive = Error.BusinessRule(
        "Identity.EmployeeInactive", "An employee who left or was anonymised cannot get a user account.");
}
