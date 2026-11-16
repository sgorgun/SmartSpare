namespace SmartSpare.SharedKernel;

/// <summary>Validation failure with messages grouped by property name.</summary>
public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error("Validation", "One or more validation errors occurred.", ErrorType.Validation);
