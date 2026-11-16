namespace SmartSpare.SharedKernel;

/// <summary>Category of an expected business error; the HTTP layer maps it to a status code.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Forbidden = 4,
    BusinessRule = 5,
}

/// <summary>
/// An expected business error. <see cref="Code"/> is stable and machine-readable
/// (for example <c>SS200</c> or <c>Inventory.InsufficientStock</c>).
/// </summary>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    public static Error BusinessRule(string code, string description) => new(code, description, ErrorType.BusinessRule);
}
